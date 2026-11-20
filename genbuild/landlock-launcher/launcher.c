#define _GNU_SOURCE

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <arpa/inet.h>
#include <poll.h>
#include <linux/landlock.h>
#include <linux/magic.h>
#include <limits.h>
#include <stddef.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/prctl.h>
#include <sys/random.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <sys/types.h>
#include <sys/un.h>
#include <sys/vfs.h>
#include <time.h>
#include <sys/wait.h>
#include <unistd.h>

#define POLICY_LIMIT (1024U * 1024U)
#define RULE_LIMIT 4096U
#define SELF_RULE_LIMIT 2U
#define BROKER_WORKER_LIMIT 1024U
#define BROKER_ARG_LIMIT 8192U
#define BROKER_ENV_LIMIT 8192U
#define BROKER_REQUEST_LIMIT (2U * 1024U * 1024U)
#define BROKER_SOCKET_PREFIX "@genbuild-"
#define BROKER_MAGIC UINT32_C(0x47424c31)
#define BROKER_RESPONSE_MAGIC UINT32_C(0x47425231)
#define BROKER_VERSION 1U
#define STAGING_ENV "MSBUILD_ACTION_STAGING_ROOT"
#define BROKER_SOCKET_ENV "MSBUILD_ACTION_BROKER_SOCKET"
#define BROKER_LAUNCHER_ENV "MSBUILD_ACTION_BROKER_LAUNCHER"
#define TMPDIR_ENV "TMPDIR"
#define TMP_ENV "TMP"
#define TEMP_ENV "TEMP"

extern char **environ;

enum broker_response_kind {
    BROKER_RESPONSE_EXIT = 1,
    BROKER_RESPONSE_SIGNAL = 2,
    BROKER_RESPONSE_ERROR = 3,
};

struct broker_request_header {
    uint32_t magic;
    uint32_t version;
    uint32_t argc;
    uint32_t envc;
    uint32_t cwd_length;
    uint32_t payload_length;
};

struct broker_response {
    uint32_t magic;
    uint32_t kind;
    uint32_t value;
};

_Static_assert(sizeof(struct broker_request_header) == 24, "broker header wire size");
_Static_assert(sizeof(struct broker_response) == 12, "broker response wire size");

struct file_rule {
    int fd;
    uint64_t access;
    dev_t device;
    ino_t inode;
    mode_t type;
};

struct policy {
    struct file_rule rules[RULE_LIMIT];
    size_t rule_count;
    const char *self_paths[SELF_RULE_LIMIT];
    size_t self_rule_count;
    char *scratch_parent;
};

struct broker_worker {
    pid_t pid;
    pid_t owner_group;
    int connection_fd;
    bool cancel_sent;
    bool leader_alive;
    int64_t kill_deadline_ms;
};

struct broker_request {
    unsigned char *payload;
    char **arguments;
    char **environment;
    char *owned_environment[5];
    char *working_directory;
    size_t argument_count;
    size_t environment_count;
};

static void
report_error(const char *message)
{
    int saved_errno = errno;
    if (saved_errno != 0) {
        (void)fprintf(stderr, "genbuild-landlock-launcher: %s: %s\n", message, strerror(saved_errno));
    } else {
        (void)fprintf(stderr, "genbuild-landlock-launcher: %s\n", message);
    }
}

static int
landlock_abi(void)
{
    return (int)syscall(__NR_landlock_create_ruleset, NULL, 0, LANDLOCK_CREATE_RULESET_VERSION);
}

static int64_t
monotonic_milliseconds(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) < 0) {
        return -1;
    }
    return (int64_t)value.tv_sec * 1000 + value.tv_nsec / 1000000;
}

static int
read_launcher_path(char **path)
{
    char buffer[PATH_MAX + 1];
    ssize_t length = readlink("/proc/self/exe", buffer, PATH_MAX);
    if (length <= 0 || length == PATH_MAX) {
        errno = length < 0 ? errno : ENAMETOOLONG;
        return -1;
    }
    buffer[length] = '\0';
    *path = strdup(buffer);
    return *path == NULL ? -1 : 0;
}

static int
create_broker_listener(int *listener_fd, char **socket_name)
{
    uint64_t random_values[2];
    ssize_t count;
    do {
        count = getrandom(random_values, sizeof(random_values), 0);
    } while (count < 0 && errno == EINTR);
    if (count != (ssize_t)sizeof(random_values)) {
        if (count >= 0) {
            errno = EIO;
        }
        return -1;
    }

    char name[sizeof(((struct sockaddr_un *)0)->sun_path)];
    int name_length = snprintf(name, sizeof(name), BROKER_SOCKET_PREFIX "%016llx%016llx",
                               (unsigned long long)random_values[0],
                               (unsigned long long)random_values[1]);
    if (name_length < 0 || (size_t)name_length >= sizeof(name)) {
        errno = ENAMETOOLONG;
        return -1;
    }

    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        return -1;
    }
    struct sockaddr_un address = {.sun_family = AF_UNIX};
    address.sun_path[0] = '\0';
    memcpy(address.sun_path + 1, name + 1, (size_t)name_length - 1);
    socklen_t address_length = (socklen_t)(offsetof(struct sockaddr_un, sun_path) +
                                           (size_t)name_length);
    if (bind(fd, (struct sockaddr *)&address, address_length) < 0 ||
        listen(fd, 128) < 0) {
        int saved_errno = errno;
        (void)close(fd);
        errno = saved_errno;
        return -1;
    }

    *socket_name = strdup(name);
    if (*socket_name == NULL) {
        (void)close(fd);
        return -1;
    }
    *listener_fd = fd;
    return 0;
}

static bool
valid_broker_socket_name(const char *name)
{
    size_t prefix_length = sizeof(BROKER_SOCKET_PREFIX) - 1;
    size_t name_length = strlen(name);
    if (name_length != prefix_length + 32 ||
        memcmp(name, BROKER_SOCKET_PREFIX, prefix_length) != 0) {
        return false;
    }
    for (size_t index = prefix_length; index < name_length; index++) {
        char character = name[index];
        if (!((character >= '0' && character <= '9') ||
              (character >= 'a' && character <= 'f'))) {
            return false;
        }
    }
    return true;
}

static int
parse_proc_number(char **cursor, pid_t *number)
{
    char *start = *cursor;
    if (*start < '0' || *start > '9') {
        return -1;
    }
    uint64_t value = 0;
    while (**cursor >= '0' && **cursor <= '9') {
        unsigned int digit = (unsigned int)(**cursor - '0');
        if (value > ((uint64_t)INT_MAX - digit) / 10) {
            return -1;
        }
        value = value * 10 + digit;
        (*cursor)++;
    }
    if (**cursor != ' ' && **cursor != '\t' && **cursor != '\n') {
        return -1;
    }
    *number = (pid_t)value;
    return 0;
}

static int
read_process_parent_and_group(pid_t process, pid_t *parent, pid_t *group)
{
    if (process <= 0) {
        errno = EINVAL;
        return -1;
    }
    char path[64];
    int path_length = snprintf(path, sizeof(path), "/proc/%ld/stat", (long)process);
    if (path_length < 0 || (size_t)path_length >= sizeof(path)) {
        errno = EOVERFLOW;
        return -1;
    }
    int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) {
        return -1;
    }
    char buffer[4096];
    size_t used = 0;
    while (used < sizeof(buffer) - 1) {
        ssize_t count = read(fd, buffer + used, sizeof(buffer) - 1 - used);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count < 0) {
            int saved_errno = errno;
            (void)close(fd);
            errno = saved_errno;
            return -1;
        }
        if (count == 0) {
            break;
        }
        used += (size_t)count;
    }
    (void)close(fd);
    if (used == sizeof(buffer) - 1) {
        errno = EOVERFLOW;
        return -1;
    }
    buffer[used] = '\0';

    char *cursor = buffer;
    pid_t reported_pid;
    if (parse_proc_number(&cursor, &reported_pid) < 0 || reported_pid != process ||
        *cursor++ != ' ' || *cursor++ != '(') {
        errno = EINVAL;
        return -1;
    }
    char *closing_parenthesis = strrchr(cursor, ')');
    if (closing_parenthesis == NULL) {
        errno = EINVAL;
        return -1;
    }
    cursor = closing_parenthesis + 1;
    while (*cursor == ' ' || *cursor == '\t') {
        cursor++;
    }
    if (*cursor == '\0' || (cursor[1] != ' ' && cursor[1] != '\t')) {
        errno = EINVAL;
        return -1;
    }
    cursor += 2;
    while (*cursor == ' ' || *cursor == '\t') {
        cursor++;
    }
    if (parse_proc_number(&cursor, parent) < 0) {
        errno = EINVAL;
        return -1;
    }
    while (*cursor == ' ' || *cursor == '\t') {
        cursor++;
    }
    if (parse_proc_number(&cursor, group) < 0) {
        errno = EINVAL;
        return -1;
    }
    return 0;
}

static bool
process_belongs_to_family(pid_t process, pid_t host_group,
                          const struct broker_worker *workers, size_t worker_count,
                          pid_t *owner_group)
{
    for (unsigned int depth = 0; depth < 64; depth++) {
        pid_t parent;
        pid_t group;
        if (read_process_parent_and_group(process, &parent, &group) < 0) {
            return false;
        }
        if (group == host_group) {
            *owner_group = host_group;
            return true;
        }
        for (size_t index = 0; index < worker_count; index++) {
            if (workers[index].pid > 0 && group == workers[index].pid &&
                !workers[index].cancel_sent) {
                *owner_group = workers[index].pid;
                return true;
            }
        }
        if (parent <= 1 || parent == process) {
            return false;
        }
        process = parent;
    }
    return false;
}

static int
connect_broker(const char *socket_name)
{
    if (!valid_broker_socket_name(socket_name)) {
        errno = EINVAL;
        return -1;
    }
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        return -1;
    }
    struct sockaddr_un address = {.sun_family = AF_UNIX};
    address.sun_path[0] = '\0';
    size_t name_length = strlen(socket_name);
    memcpy(address.sun_path + 1, socket_name + 1, name_length - 1);
    socklen_t address_length = (socklen_t)(offsetof(struct sockaddr_un, sun_path) + name_length);
    if (connect(fd, (struct sockaddr *)&address, address_length) < 0) {
        int saved_errno = errno;
        (void)close(fd);
        errno = saved_errno;
        return -1;
    }
    return fd;
}

static int
send_all(int fd, const void *buffer, size_t length)
{
    const unsigned char *cursor = buffer;
    while (length > 0) {
        ssize_t count = send(fd, cursor, length, MSG_NOSIGNAL);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            if (count == 0) {
                errno = EPIPE;
            }
            return -1;
        }
        cursor += (size_t)count;
        length -= (size_t)count;
    }
    return 0;
}

static int
receive_all(int fd, void *buffer, size_t length)
{
    unsigned char *cursor = buffer;
    while (length > 0) {
        ssize_t count = recv(fd, cursor, length, 0);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            if (count == 0) {
                errno = ECONNRESET;
            }
            return -1;
        }
        cursor += (size_t)count;
        length -= (size_t)count;
    }
    return 0;
}

static void
close_received_fds(int *fds, size_t count)
{
    for (size_t index = 0; index < count; index++) {
        if (fds[index] >= 0) {
            (void)close(fds[index]);
            fds[index] = -1;
        }
    }
}

static int
collect_control_fds(struct msghdr *message, int *fds, size_t *fd_count, bool allow_fds)
{
    bool invalid = (message->msg_flags & MSG_CTRUNC) != 0;
    for (struct cmsghdr *header = CMSG_FIRSTHDR(message); header != NULL;
         header = CMSG_NXTHDR(message, header)) {
        if (header->cmsg_level != SOL_SOCKET || header->cmsg_type != SCM_RIGHTS ||
            header->cmsg_len < CMSG_LEN(0)) {
            invalid = true;
            continue;
        }
        size_t bytes = header->cmsg_len - CMSG_LEN(0);
        if (bytes % sizeof(int) != 0) {
            invalid = true;
            continue;
        }
        size_t count = bytes / sizeof(int);
        int *incoming = (int *)CMSG_DATA(header);
        for (size_t index = 0; index < count; index++) {
            if (allow_fds && *fd_count < 2) {
                fds[(*fd_count)++] = incoming[index];
            } else {
                (void)close(incoming[index]);
                invalid = true;
            }
        }
    }
    return invalid ? -1 : 0;
}

static int
receive_header_and_fds(int connection_fd, struct broker_request_header *request, int fds[2])
{
    size_t used = 0;
    size_t fd_count = 0;
    fds[0] = -1;
    fds[1] = -1;
    while (used < sizeof(*request)) {
        char control[CMSG_SPACE(sizeof(int) * 8)] = {0};
        struct iovec vector = {
            .iov_base = (unsigned char *)request + used,
            .iov_len = sizeof(*request) - used,
        };
        struct msghdr message = {
            .msg_iov = &vector,
            .msg_iovlen = 1,
            .msg_control = control,
            .msg_controllen = sizeof(control),
        };
        ssize_t count = recvmsg(connection_fd, &message, MSG_CMSG_CLOEXEC);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0 || collect_control_fds(&message, fds, &fd_count, true) < 0) {
            close_received_fds(fds, fd_count);
            if (count == 0) {
                errno = ECONNRESET;
            }
            return -1;
        }
        used += (size_t)count;
    }
    if (fd_count != 2) {
        close_received_fds(fds, fd_count);
        errno = EINVAL;
        return -1;
    }
    return 0;
}

static int
receive_payload(int connection_fd, unsigned char *payload, size_t length)
{
    size_t used = 0;
    while (used < length) {
        char control[CMSG_SPACE(sizeof(int) * 8)] = {0};
        struct iovec vector = {
            .iov_base = payload + used,
            .iov_len = length - used,
        };
        struct msghdr message = {
            .msg_iov = &vector,
            .msg_iovlen = 1,
            .msg_control = control,
            .msg_controllen = sizeof(control),
        };
        ssize_t count = recvmsg(connection_fd, &message, MSG_CMSG_CLOEXEC);
        int ignored_fds[2] = {-1, -1};
        size_t ignored_count = 0;
        int control_result = collect_control_fds(&message, ignored_fds, &ignored_count, false);
        close_received_fds(ignored_fds, ignored_count);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0 || control_result < 0) {
            if (count == 0) {
                errno = ECONNRESET;
            }
            return -1;
        }
        used += (size_t)count;
    }
    return 0;
}

static int
send_broker_response(int connection_fd, uint32_t kind, uint32_t value)
{
    struct broker_response response = {
        .magic = htonl(BROKER_RESPONSE_MAGIC),
        .kind = htonl(kind),
        .value = htonl(value),
    };
    return send_all(connection_fd, &response, sizeof(response));
}

static bool
environment_key_matches(const char *entry, const char *key)
{
    const char *separator = strchr(entry, '=');
    return separator != NULL && (size_t)(separator - entry) == strlen(key) &&
           memcmp(entry, key, (size_t)(separator - entry)) == 0;
}

static bool
valid_environment_entry(const char *entry)
{
    const char *separator = strchr(entry, '=');
    return separator != NULL && separator != entry;
}

static bool
reserved_environment_entry(const char *entry)
{
    return environment_key_matches(entry, STAGING_ENV) ||
           environment_key_matches(entry, BROKER_SOCKET_ENV) ||
           environment_key_matches(entry, BROKER_LAUNCHER_ENV) ||
           environment_key_matches(entry, TMPDIR_ENV) ||
           environment_key_matches(entry, TMP_ENV) ||
           environment_key_matches(entry, TEMP_ENV);
}

static char *
environment_assignment(const char *key, const char *value)
{
    size_t key_length = strlen(key);
    size_t value_length = strlen(value);
    if (key_length > SIZE_MAX - value_length - 2) {
        errno = EOVERFLOW;
        return NULL;
    }
    char *result = malloc(key_length + value_length + 2);
    if (result == NULL) {
        return NULL;
    }
    memcpy(result, key, key_length);
    result[key_length] = '=';
    memcpy(result + key_length + 1, value, value_length + 1);
    return result;
}

static void
free_broker_request(struct broker_request *request)
{
    free(request->payload);
    free(request->arguments);
    free(request->environment);
    for (size_t index = 0; index < sizeof(request->owned_environment) /
                                      sizeof(request->owned_environment[0]); index++) {
        free(request->owned_environment[index]);
    }
    memset(request, 0, sizeof(*request));
}

static int
parse_broker_request(int connection_fd, const struct broker_request_header *wire_header,
                     const char *socket_name, const char *launcher_path,
                     const char *scratch_path, struct broker_request *request)
{
    uint32_t magic = ntohl(wire_header->magic);
    uint32_t version = ntohl(wire_header->version);
    uint32_t argument_count = ntohl(wire_header->argc);
    uint32_t environment_count = ntohl(wire_header->envc);
    uint32_t cwd_length = ntohl(wire_header->cwd_length);
    uint32_t payload_length = ntohl(wire_header->payload_length);
    if (magic != BROKER_MAGIC || version != BROKER_VERSION ||
        argument_count == 0 || argument_count > BROKER_ARG_LIMIT ||
        environment_count > BROKER_ENV_LIMIT || cwd_length < 2 ||
        cwd_length > 65536 || payload_length < cwd_length ||
        payload_length > BROKER_REQUEST_LIMIT) {
        errno = EINVAL;
        return -1;
    }

    request->payload = malloc(payload_length);
    if (request->payload == NULL) {
        return -1;
    }
    if (receive_payload(connection_fd, request->payload, payload_length) < 0) {
        return -1;
    }

    char trailing_byte;
    ssize_t trailing = recv(connection_fd, &trailing_byte, sizeof(trailing_byte),
                            MSG_PEEK | MSG_DONTWAIT);
    if (trailing > 0 || (trailing < 0 && errno != EAGAIN && errno != EWOULDBLOCK &&
                         errno != EINTR)) {
        errno = EINVAL;
        return -1;
    }

    if (request->payload[cwd_length - 1] != '\0' ||
        memchr(request->payload, '\0', cwd_length - 1) != NULL ||
        request->payload[0] != '/') {
        errno = EINVAL;
        return -1;
    }
    request->working_directory = (char *)request->payload;
    request->argument_count = argument_count;
    request->environment_count = environment_count;
    request->arguments = calloc((size_t)argument_count + 1, sizeof(char *));
    request->environment = calloc((size_t)environment_count + 6 + 1, sizeof(char *));
    if (request->arguments == NULL || request->environment == NULL) {
        return -1;
    }

    size_t offset = cwd_length;
    for (size_t index = 0; index < argument_count; index++) {
        if (offset >= payload_length) {
            errno = EINVAL;
            return -1;
        }
        char *value = (char *)request->payload + offset;
        unsigned char *end = memchr(request->payload + offset, '\0', payload_length - offset);
        if (end == NULL) {
            errno = EINVAL;
            return -1;
        }
        request->arguments[index] = value;
        offset = (size_t)(end - request->payload) + 1;
    }
    if (request->arguments[0][0] != '/') {
        errno = EINVAL;
        return -1;
    }

    size_t output_environment_count = 0;
    for (size_t index = 0; index < environment_count; index++) {
        if (offset >= payload_length) {
            errno = EINVAL;
            return -1;
        }
        char *value = (char *)request->payload + offset;
        unsigned char *end = memchr(request->payload + offset, '\0', payload_length - offset);
        if (end == NULL || !valid_environment_entry(value)) {
            errno = EINVAL;
            return -1;
        }
        if (!reserved_environment_entry(value)) {
            request->environment[output_environment_count++] = value;
        }
        offset = (size_t)(end - request->payload) + 1;
    }
    if (offset != payload_length) {
        errno = EINVAL;
        return -1;
    }

    const char *keys[] = {BROKER_SOCKET_ENV, BROKER_LAUNCHER_ENV,
                          TMPDIR_ENV, TMP_ENV, TEMP_ENV};
    const char *values[] = {socket_name, launcher_path, scratch_path, scratch_path, scratch_path};
    for (size_t index = 0; index < sizeof(keys) / sizeof(keys[0]); index++) {
        request->owned_environment[index] = environment_assignment(keys[index], values[index]);
        if (request->owned_environment[index] == NULL) {
            return -1;
        }
        request->environment[output_environment_count++] = request->owned_environment[index];
    }
    request->environment[output_environment_count] = NULL;
    return 0;
}

static int
add_string_size(size_t *total, const char *value)
{
    size_t length = strnlen(value, BROKER_REQUEST_LIMIT + 1U);
    if (length > BROKER_REQUEST_LIMIT || length + 1 > BROKER_REQUEST_LIMIT - *total) {
        errno = E2BIG;
        return -1;
    }
    *total += length + 1;
    return 0;
}

static void
append_string(unsigned char **cursor, const char *value)
{
    size_t length = strlen(value) + 1;
    memcpy(*cursor, value, length);
    *cursor += length;
}

static int
broker_client(const char *socket_name, char **program_arguments)
{
    if (!valid_broker_socket_name(socket_name) || program_arguments[0] == NULL ||
        program_arguments[0][0] != '/') {
        errno = EINVAL;
        report_error("broker mode requires an absolute executable and valid socket name");
        return 2;
    }

    size_t argument_count = 0;
    size_t environment_count = 0;
    size_t payload_length = 0;
    for (char **argument = program_arguments; *argument != NULL; argument++) {
        if (++argument_count > BROKER_ARG_LIMIT || add_string_size(&payload_length, *argument) < 0) {
            report_error("broker argument vector exceeds its limit");
            return 2;
        }
    }
    for (char **entry = environ; *entry != NULL; entry++) {
        if (++environment_count > BROKER_ENV_LIMIT || !valid_environment_entry(*entry) ||
            add_string_size(&payload_length, *entry) < 0) {
            errno = EINVAL;
            report_error("broker environment exceeds its limit or contains an invalid entry");
            return 2;
        }
    }

    char *working_directory = getcwd(NULL, 0);
    if (working_directory == NULL) {
        report_error("cannot capture broker working directory");
        return 126;
    }
    size_t cwd_length = strlen(working_directory) + 1;
    if (working_directory[0] != '/' || cwd_length > 65536 ||
        cwd_length > BROKER_REQUEST_LIMIT - payload_length) {
        errno = E2BIG;
        report_error("broker working directory exceeds its limit");
        free(working_directory);
        return 2;
    }
    payload_length += cwd_length;

    unsigned char *payload = malloc(payload_length);
    if (payload == NULL) {
        report_error("cannot allocate broker request");
        free(working_directory);
        return 126;
    }
    unsigned char *cursor = payload;
    append_string(&cursor, working_directory);
    for (char **argument = program_arguments; *argument != NULL; argument++) {
        append_string(&cursor, *argument);
    }
    for (char **entry = environ; *entry != NULL; entry++) {
        append_string(&cursor, *entry);
    }
    free(working_directory);

    struct broker_request_header header = {
        .magic = htonl(BROKER_MAGIC),
        .version = htonl(BROKER_VERSION),
        .argc = htonl((uint32_t)argument_count),
        .envc = htonl((uint32_t)environment_count),
        .cwd_length = htonl((uint32_t)cwd_length),
        .payload_length = htonl((uint32_t)payload_length),
    };
    int connection_fd = connect_broker(socket_name);
    if (connection_fd < 0) {
        report_error("cannot connect to action broker");
        free(payload);
        return 126;
    }

    char control[CMSG_SPACE(sizeof(int) * 2)] = {0};
    struct iovec vector = {.iov_base = &header, .iov_len = sizeof(header)};
    struct msghdr message = {
        .msg_iov = &vector,
        .msg_iovlen = 1,
        .msg_control = control,
        .msg_controllen = sizeof(control),
    };
    struct cmsghdr *control_header = CMSG_FIRSTHDR(&message);
    control_header->cmsg_level = SOL_SOCKET;
    control_header->cmsg_type = SCM_RIGHTS;
    control_header->cmsg_len = CMSG_LEN(sizeof(int) * 2);
    int standard_fds[2] = {STDOUT_FILENO, STDERR_FILENO};
    memcpy(CMSG_DATA(control_header), standard_fds, sizeof(standard_fds));
    ssize_t sent;
    do {
        sent = sendmsg(connection_fd, &message, MSG_NOSIGNAL);
    } while (sent < 0 && errno == EINTR);
    if (sent <= 0 ||
        send_all(connection_fd, (unsigned char *)&header + (size_t)sent,
                 sizeof(header) - (size_t)sent) < 0 ||
        send_all(connection_fd, payload, payload_length) < 0) {
        report_error("cannot send broker request");
        (void)close(connection_fd);
        free(payload);
        return 126;
    }
    free(payload);

    struct broker_response response;
    if (receive_all(connection_fd, &response, sizeof(response)) < 0) {
        report_error("broker disconnected before returning process status");
        (void)close(connection_fd);
        return 126;
    }
    (void)close(connection_fd);
    uint32_t response_magic = ntohl(response.magic);
    uint32_t response_kind = ntohl(response.kind);
    uint32_t response_value = ntohl(response.value);
    if (response_magic != BROKER_RESPONSE_MAGIC ||
        (response_kind != BROKER_RESPONSE_EXIT && response_kind != BROKER_RESPONSE_SIGNAL &&
         response_kind != BROKER_RESPONSE_ERROR)) {
        errno = EPROTO;
        report_error("broker returned an invalid response");
        return 126;
    }
    if (response_kind == BROKER_RESPONSE_ERROR) {
        (void)fprintf(stderr, "genbuild-landlock-launcher: broker rejected process request\n");
        return 126;
    }
    if (response_kind == BROKER_RESPONSE_EXIT) {
        if (response_value > 255) {
            errno = EPROTO;
            report_error("broker returned an invalid exit status");
            return 126;
        }
        return (int)response_value;
    }
    if (response_value == 0 || response_value >= NSIG) {
        errno = EPROTO;
        report_error("broker returned an invalid signal status");
        return 126;
    }
    (void)signal((int)response_value, SIG_DFL);
    (void)raise((int)response_value);
    return 128 + (int)response_value;
}

static bool
valid_utf8(const unsigned char *text, size_t length)
{
    size_t index = 0;
    while (index < length) {
        unsigned char first = text[index++];
        if (first < 0x80) {
            continue;
        }

        unsigned int continuation_count;
        uint32_t value;
        if (first >= 0xc2 && first <= 0xdf) {
            continuation_count = 1;
            value = (uint32_t)(first & 0x1f);
        } else if (first >= 0xe0 && first <= 0xef) {
            continuation_count = 2;
            value = (uint32_t)(first & 0x0f);
        } else if (first >= 0xf0 && first <= 0xf4) {
            continuation_count = 3;
            value = (uint32_t)(first & 0x07);
        } else {
            return false;
        }

        if (length - index < continuation_count) {
            return false;
        }
        for (unsigned int count = 0; count < continuation_count; count++) {
            unsigned char next = text[index++];
            if ((next & 0xc0) != 0x80) {
                return false;
            }
            value = (value << 6) | (uint32_t)(next & 0x3f);
        }
        if ((continuation_count == 2 && value < 0x800) ||
            (continuation_count == 3 && value < 0x10000) ||
            (value >= 0xd800 && value <= 0xdfff) || value > 0x10ffff) {
            return false;
        }
    }
    return true;
}

static int
read_policy_file(const char *path, unsigned char **data, size_t *length)
{
    if (path[0] != '/') {
        errno = EINVAL;
        report_error("policy path must be absolute");
        return -1;
    }

    int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) {
        report_error("cannot open policy file");
        return -1;
    }

    struct stat status;
    if (fstat(fd, &status) < 0) {
        report_error("cannot inspect policy file");
        (void)close(fd);
        return -1;
    }
    if (!S_ISREG(status.st_mode) || status.st_size < 1 ||
        (uintmax_t)status.st_size > POLICY_LIMIT) {
        errno = EINVAL;
        report_error("policy must be a non-empty regular file no larger than 1 MiB");
        (void)close(fd);
        return -1;
    }

    size_t capacity = (size_t)status.st_size;
    unsigned char *contents = malloc(capacity);
    if (contents == NULL) {
        report_error("cannot allocate policy buffer");
        (void)close(fd);
        return -1;
    }

    size_t used = 0;
    while (used < capacity) {
        ssize_t count = read(fd, contents + used, capacity - used);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            if (count == 0) {
                errno = EINVAL;
            }
            report_error("cannot read complete policy file");
            free(contents);
            (void)close(fd);
            return -1;
        }
        used += (size_t)count;
    }
    (void)close(fd);

    if (contents[capacity - 1] != '\0') {
        errno = EINVAL;
        report_error("policy is missing its final NUL delimiter");
        free(contents);
        return -1;
    }

    *data = contents;
    *length = capacity;
    return 0;
}

static int
next_record(unsigned char *data, size_t length, size_t *offset, char **record)
{
    if (*offset >= length) {
        return 0;
    }
    unsigned char *start = data + *offset;
    unsigned char *end = memchr(start, '\0', length - *offset);
    if (end == NULL || end == start || !valid_utf8(start, (size_t)(end - start))) {
        errno = EINVAL;
        return -1;
    }
    *record = (char *)start;
    *offset = (size_t)(end - data) + 1;
    return 1;
}

static int
open_directory_rule(const char *path, struct stat *status)
{
    if (path[0] != '/' || path[1] == '\0') {
        errno = EINVAL;
        return -1;
    }

    char *canonical = realpath(path, NULL);
    if (canonical == NULL) {
        return -1;
    }
    bool same_path = strcmp(canonical, path) == 0;
    free(canonical);
    if (!same_path) {
        errno = EINVAL;
        return -1;
    }

    int fd = open(path, O_PATH | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) {
        return -1;
    }
    struct statfs filesystem;
    if (fstat(fd, status) < 0 || fstatfs(fd, &filesystem) < 0) {
        (void)close(fd);
        return -1;
    }
    if (!S_ISDIR(status->st_mode) || (unsigned long)filesystem.f_type == PROC_SUPER_MAGIC) {
        errno = EINVAL;
        (void)close(fd);
        return -1;
    }
    return fd;
}

static int
directory_is_empty(int directory_fd)
{
    int scan_fd = openat(directory_fd, ".", O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (scan_fd < 0) {
        return -1;
    }
    DIR *directory = fdopendir(scan_fd);
    if (directory == NULL) {
        (void)close(scan_fd);
        return -1;
    }

    int result = 1;
    struct dirent *entry;
    errno = 0;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") != 0 && strcmp(entry->d_name, "..") != 0) {
            result = 0;
            break;
        }
    }
    if (entry == NULL && errno != 0) {
        result = -1;
    }
    int saved_errno = errno;
    (void)closedir(directory);
    errno = saved_errno;
    return result;
}

static int
add_file_rule(struct policy *policy, const char *path, uint64_t access)
{
    if (path[0] != '/') {
        errno = EINVAL;
        return -1;
    }
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) {
        return -1;
    }
    struct stat status;
    struct statfs filesystem;
    if (fstat(fd, &status) < 0 || fstatfs(fd, &filesystem) < 0) {
        (void)close(fd);
        return -1;
    }
    bool procfs = (unsigned long)filesystem.f_type == PROC_SUPER_MAGIC;
    bool approved_proc_setting =
        procfs && access == LANDLOCK_ACCESS_FS_READ_FILE &&
        (strcmp(path, "/proc/meminfo") == 0 ||
         strcmp(path, "/proc/sys/vm/overcommit_memory") == 0);
    if (!S_ISREG(status.st_mode) ||
        (procfs && !approved_proc_setting) ||
        (!procfs && (strcmp(path, "/proc/meminfo") == 0 ||
                     strcmp(path, "/proc/sys/vm/overcommit_memory") == 0))) {
        errno = EINVAL;
        (void)close(fd);
        return -1;
    }

    for (size_t index = 0; index < policy->rule_count; index++) {
        struct file_rule *existing = &policy->rules[index];
        if (existing->device == status.st_dev && existing->inode == status.st_ino &&
            existing->type == (status.st_mode & S_IFMT)) {
            existing->access |= access;
            (void)close(fd);
            return 0;
        }
    }
    if (policy->rule_count == RULE_LIMIT) {
        errno = E2BIG;
        (void)close(fd);
        return -1;
    }
    policy->rules[policy->rule_count++] = (struct file_rule){
        .fd = fd,
        .access = access,
        .device = status.st_dev,
        .inode = status.st_ino,
        .type = status.st_mode & S_IFMT,
    };
    return 0;
}

static int
add_directory_rule(struct policy *policy, const char *path)
{
    struct stat status;
    int fd = open_directory_rule(path, &status);
    if (fd < 0) {
        return -1;
    }

    for (size_t index = 0; index < policy->rule_count; index++) {
        struct file_rule *existing = &policy->rules[index];
        if (existing->device == status.st_dev && existing->inode == status.st_ino &&
            existing->type == (status.st_mode & S_IFMT)) {
            existing->access |= LANDLOCK_ACCESS_FS_READ_DIR;
            (void)close(fd);
            return 0;
        }
    }
    if (policy->rule_count == RULE_LIMIT) {
        errno = E2BIG;
        (void)close(fd);
        return -1;
    }
    policy->rules[policy->rule_count++] = (struct file_rule){
        .fd = fd,
        .access = LANDLOCK_ACCESS_FS_READ_DIR,
        .device = status.st_dev,
        .inode = status.st_ino,
        .type = status.st_mode & S_IFMT,
    };
    return 0;
}

static int
parse_policy(const char *path, struct policy *policy)
{
    unsigned char *data = NULL;
    size_t length = 0;
    if (read_policy_file(path, &data, &length) < 0) {
        return -1;
    }

    size_t offset = 0;
    char *record = NULL;
    int result = next_record(data, length, &offset, &record);
    if (result != 1 || strcmp(record, "genbuild-landlock-v1") != 0) {
        errno = EINVAL;
        report_error("invalid policy header or version");
        free(data);
        return -1;
    }

    size_t scratch_parent_count = 0;
    while (offset < length) {
        char *kind = NULL;
        char *entry_path = NULL;
        if (next_record(data, length, &offset, &kind) != 1 ||
            next_record(data, length, &offset, &entry_path) != 1 ||
            entry_path[0] != '/') {
            errno = EINVAL;
            report_error("policy contains an incomplete record or non-absolute path");
            free(data);
            return -1;
        }

        if (strcmp(kind, "read-file") == 0) {
            if (add_file_rule(policy, entry_path, LANDLOCK_ACCESS_FS_READ_FILE) < 0) {
                report_error("read-file path must resolve to an existing regular file");
                free(data);
                return -1;
            }
        } else if (strcmp(kind, "execute-file") == 0) {
            uint64_t execute_access = LANDLOCK_ACCESS_FS_EXECUTE | LANDLOCK_ACCESS_FS_READ_FILE;
            if (add_file_rule(policy, entry_path, execute_access) < 0) {
                report_error("execute-file path must resolve to an existing regular file");
                free(data);
                return -1;
            }
        } else if (strcmp(kind, "read-directory") == 0) {
            if (add_directory_rule(policy, entry_path) < 0) {
                report_error("read-directory path must be a canonical, non-root directory");
                free(data);
                return -1;
            }
        } else if (strcmp(kind, "read-self-file") == 0) {
            if ((strcmp(entry_path, "/proc/self/maps") != 0 &&
                 strcmp(entry_path, "/proc/self/mountinfo") != 0) ||
                policy->self_rule_count == SELF_RULE_LIMIT) {
                errno = EINVAL;
                report_error("read-self-file is limited to /proc/self/maps and /proc/self/mountinfo");
                free(data);
                return -1;
            }
            bool duplicate = false;
            for (size_t index = 0; index < policy->self_rule_count; index++) {
                if (strcmp(policy->self_paths[index], entry_path) == 0) {
                    duplicate = true;
                }
            }
            if (!duplicate) {
                policy->self_paths[policy->self_rule_count++] =
                    strcmp(entry_path, "/proc/self/maps") == 0 ? "/proc/self/maps" : "/proc/self/mountinfo";
            }
        } else if (strcmp(kind, "scratch-parent") == 0) {
            if (++scratch_parent_count != 1 || entry_path[1] == '\0') {
                errno = EINVAL;
                report_error("policy must contain exactly one non-root scratch-parent");
                free(data);
                return -1;
            }
            struct stat status;
            int fd = open_directory_rule(entry_path, &status);
            if (fd < 0) {
                report_error("scratch-parent must be a canonical, non-root directory");
                free(data);
                return -1;
            }
            (void)close(fd);
            policy->scratch_parent = strdup(entry_path);
            if (policy->scratch_parent == NULL) {
                report_error("cannot allocate scratch-parent path");
                free(data);
                return -1;
            }
        } else {
            errno = EINVAL;
            report_error("policy contains an unknown record kind");
            free(data);
            return -1;
        }
    }

    free(data);
    if (scratch_parent_count != 1) {
        errno = EINVAL;
        report_error("policy must contain exactly one scratch-parent");
        return -1;
    }
    return 0;
}

static void
close_policy(struct policy *policy)
{
    for (size_t index = 0; index < policy->rule_count; index++) {
        if (policy->rules[index].fd >= 0) {
            (void)close(policy->rules[index].fd);
            policy->rules[index].fd = -1;
        }
    }
}

static int
open_self_rule_fds(const struct policy *policy, int self_fds[SELF_RULE_LIMIT])
{
    for (size_t index = 0; index < SELF_RULE_LIMIT; index++) {
        self_fds[index] = -1;
    }
    for (size_t index = 0; index < policy->self_rule_count; index++) {
        int fd = open(policy->self_paths[index], O_PATH | O_CLOEXEC);
        if (fd < 0) {
            report_error("cannot open a declared process-local file");
            return -1;
        }
        struct stat status;
        if (fstat(fd, &status) < 0 || !S_ISREG(status.st_mode)) {
            if (errno == 0) {
                errno = EINVAL;
            }
            report_error("declared process-local path is not a regular file");
            (void)close(fd);
            return -1;
        }
        self_fds[index] = fd;
    }
    return 0;
}

static void
close_self_rule_fds(int self_fds[SELF_RULE_LIMIT])
{
    for (size_t index = 0; index < SELF_RULE_LIMIT; index++) {
        if (self_fds[index] >= 0) {
            (void)close(self_fds[index]);
            self_fds[index] = -1;
        }
    }
}

static int
make_private_scratch(const char *parent_path, int *parent_fd, int *scratch_fd,
                     char **scratch_path, char **scratch_name, struct stat *scratch_status)
{
    struct stat parent_status;
    int dirfd = open_directory_rule(parent_path, &parent_status);
    if (dirfd < 0) {
        report_error("cannot open scratch-parent");
        return -1;
    }
    int writable_parent = open(parent_path, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (writable_parent < 0) {
        report_error("cannot open scratch-parent for creation");
        (void)close(dirfd);
        return -1;
    }
    (void)close(dirfd);

    char name[64];
    bool created = false;
    for (unsigned int attempt = 0; attempt < 64; attempt++) {
        uint64_t random_value;
        ssize_t count;
        do {
            count = getrandom(&random_value, sizeof(random_value), 0);
        } while (count < 0 && errno == EINTR);
        if (count != (ssize_t)sizeof(random_value)) {
            if (count >= 0) {
                errno = EIO;
            }
            report_error("cannot generate scratch directory name");
            (void)close(writable_parent);
            return -1;
        }
        (void)snprintf(name, sizeof(name), ".genbuild-action-%016llx",
                       (unsigned long long)random_value);
        if (mkdirat(writable_parent, name, 0700) == 0) {
            created = true;
            break;
        }
        if (errno != EEXIST) {
            report_error("cannot create private scratch directory");
            (void)close(writable_parent);
            return -1;
        }
    }
    if (!created) {
        errno = EEXIST;
        report_error("cannot find a unique scratch directory name");
        (void)close(writable_parent);
        return -1;
    }

    int root_access_fd = openat(writable_parent, name,
                                O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (root_access_fd < 0 || fchmod(root_access_fd, 0700) < 0 ||
        fstat(root_access_fd, scratch_status) < 0 ||
        !S_ISDIR(scratch_status->st_mode) || scratch_status->st_uid != geteuid() ||
        (scratch_status->st_mode & 0777) != 0700 || directory_is_empty(root_access_fd) != 1) {
        report_error("cannot open created scratch directory");
        if (root_access_fd >= 0) {
            (void)close(root_access_fd);
        }
        (void)unlinkat(writable_parent, name, AT_REMOVEDIR);
        (void)close(writable_parent);
        return -1;
    }
    int root_fd = openat(writable_parent, name, O_PATH | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    (void)close(root_access_fd);
    if (root_fd < 0) {
        report_error("cannot anchor created scratch directory");
        (void)unlinkat(writable_parent, name, AT_REMOVEDIR);
        (void)close(writable_parent);
        return -1;
    }

    size_t path_size = strlen(parent_path) + strlen(name) + 2;
    char *absolute_path = malloc(path_size);
    char *owned_name = strdup(name);
    if (absolute_path == NULL || owned_name == NULL) {
        errno = ENOMEM;
        report_error("cannot allocate scratch directory path");
        free(absolute_path);
        free(owned_name);
        (void)close(root_fd);
        (void)unlinkat(writable_parent, name, AT_REMOVEDIR);
        (void)close(writable_parent);
        return -1;
    }
    (void)snprintf(absolute_path, path_size, "%s/%s", parent_path, name);

    *parent_fd = writable_parent;
    *scratch_fd = root_fd;
    *scratch_path = absolute_path;
    *scratch_name = owned_name;
    return 0;
}

static int
remove_directory_contents(int directory_fd)
{
    int scan_fd = openat(directory_fd, ".", O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (scan_fd < 0) {
        return -1;
    }
    DIR *directory = fdopendir(scan_fd);
    if (directory == NULL) {
        (void)close(scan_fd);
        return -1;
    }

    int result = 0;
    struct dirent *entry;
    errno = 0;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            errno = 0;
            continue;
        }

        struct stat status;
        if (fstatat(directory_fd, entry->d_name, &status, AT_SYMLINK_NOFOLLOW) < 0) {
            if (errno == ENOENT) {
                errno = 0;
                continue;
            }
            result = -1;
            break;
        }
        if (S_ISDIR(status.st_mode)) {
            int child_fd = openat(directory_fd, entry->d_name,
                                  O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
            if (child_fd < 0) {
                result = -1;
                break;
            }
            if (remove_directory_contents(child_fd) < 0) {
                (void)close(child_fd);
                result = -1;
                break;
            }
            (void)close(child_fd);
            if (unlinkat(directory_fd, entry->d_name, AT_REMOVEDIR) < 0 && errno != ENOENT) {
                result = -1;
                break;
            }
        } else if (unlinkat(directory_fd, entry->d_name, 0) < 0 && errno != ENOENT) {
            result = -1;
            break;
        }
        errno = 0;
    }
    if (entry == NULL && errno != 0) {
        result = -1;
    }
    int saved_errno = errno;
    (void)closedir(directory);
    errno = saved_errno;
    return result;
}

static void
cleanup_scratch(int parent_fd, int scratch_fd, const char *scratch_name,
                const struct stat *original_status)
{
    struct stat current;
    if (fstatat(parent_fd, scratch_name, &current, AT_SYMLINK_NOFOLLOW) < 0 ||
        !S_ISDIR(current.st_mode) || current.st_dev != original_status->st_dev ||
        current.st_ino != original_status->st_ino) {
        return;
    }
    if (remove_directory_contents(scratch_fd) < 0) {
        report_error("could not completely clean private scratch directory");
        return;
    }
    if (unlinkat(parent_fd, scratch_name, AT_REMOVEDIR) < 0 && errno != ENOENT) {
        report_error("could not remove private scratch directory");
    }
}

static int
add_landlock_rules(const struct policy *policy, const int self_fds[SELF_RULE_LIMIT],
                   int scratch_fd, int *ruleset_fd)
{
    struct landlock_ruleset_attr ruleset = {
        .handled_access_fs = LANDLOCK_ACCESS_FS_EXECUTE |
                             LANDLOCK_ACCESS_FS_READ_FILE |
                             LANDLOCK_ACCESS_FS_READ_DIR,
    };
    int fd = (int)syscall(__NR_landlock_create_ruleset, &ruleset, sizeof(ruleset), 0);
    if (fd < 0) {
        report_error("cannot create Landlock ruleset");
        return -1;
    }

    for (size_t index = 0; index < policy->rule_count; index++) {
        struct landlock_path_beneath_attr rule = {
            .allowed_access = policy->rules[index].access,
            .parent_fd = policy->rules[index].fd,
        };
        if (syscall(__NR_landlock_add_rule, fd, LANDLOCK_RULE_PATH_BENEATH, &rule, 0) < 0) {
            report_error("cannot add policy path rule");
            (void)close(fd);
            return -1;
        }
    }

    for (size_t index = 0; index < policy->self_rule_count; index++) {
        struct landlock_path_beneath_attr rule = {
            .allowed_access = LANDLOCK_ACCESS_FS_READ_FILE,
            .parent_fd = self_fds[index],
        };
        if (syscall(__NR_landlock_add_rule, fd, LANDLOCK_RULE_PATH_BENEATH, &rule, 0) < 0) {
            report_error("cannot add process-local path rule");
            (void)close(fd);
            return -1;
        }
    }

    struct landlock_path_beneath_attr scratch_rule = {
        .allowed_access = LANDLOCK_ACCESS_FS_READ_FILE |
                          LANDLOCK_ACCESS_FS_READ_DIR |
                          LANDLOCK_ACCESS_FS_EXECUTE,
        .parent_fd = scratch_fd,
    };
    if (syscall(__NR_landlock_add_rule, fd, LANDLOCK_RULE_PATH_BENEATH, &scratch_rule, 0) < 0) {
        report_error("cannot add private scratch rule");
        (void)close(fd);
        return -1;
    }
    *ruleset_fd = fd;
    return 0;
}

static void close_child_descriptors(void);

static int
set_connection_timeout(int fd)
{
    struct timeval timeout = {.tv_sec = 10, .tv_usec = 0};
    if (setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) < 0 ||
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout)) < 0) {
        return -1;
    }
    return 0;
}

static void
reject_broker_connection(int fd, int received_fds[2])
{
    (void)send_broker_response(fd, BROKER_RESPONSE_ERROR, 0);
    close_received_fds(received_fds, 2);
    (void)close(fd);
}

static int
spawn_broker_worker(const struct policy *policy, struct broker_request *request,
                    int received_fds[2], pid_t owner_group, int connection_fd, int listener_fd,
                    int cwd_fd, int scratch_fd, int parent_fd,
                    struct broker_worker *workers, size_t *worker_count)
{
    (void)listener_fd;
    if (*worker_count >= BROKER_WORKER_LIMIT) {
        errno = EAGAIN;
        return -1;
    }

    pid_t child = fork();
    if (child < 0) {
        return -1;
    }
    if (child == 0) {
        if (setpgid(0, 0) < 0 || fchdir(cwd_fd) < 0 ||
            dup2(received_fds[0], STDOUT_FILENO) < 0 ||
            dup2(received_fds[1], STDERR_FILENO) < 0) {
            report_error("cannot set up brokered process");
            _exit(126);
        }
        int null_fd = open("/dev/null", O_RDONLY | O_CLOEXEC);
        if (null_fd < 0 || dup2(null_fd, STDIN_FILENO) < 0) {
            report_error("cannot detach brokered process standard input");
            _exit(126);
        }
        if (null_fd != STDIN_FILENO) {
            (void)close(null_fd);
        }

        int self_fds[SELF_RULE_LIMIT];
        if (open_self_rule_fds(policy, self_fds) < 0) {
            _exit(126);
        }
        int ruleset_fd = -1;
        if (add_landlock_rules(policy, self_fds, scratch_fd, &ruleset_fd) < 0) {
            close_self_rule_fds(self_fds);
            _exit(126);
        }
        if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) < 0) {
            report_error("cannot set no_new_privs for brokered process");
            _exit(126);
        }
        if (syscall(__NR_landlock_restrict_self, ruleset_fd, 0) < 0) {
            report_error("cannot enforce brokered Landlock ruleset");
            _exit(126);
        }
        (void)close(ruleset_fd);
        close_self_rule_fds(self_fds);
        close_policy((struct policy *)policy);
        (void)close(cwd_fd);
        (void)close(scratch_fd);
        (void)close(parent_fd);
        (void)close(connection_fd);
        close_child_descriptors();
        execve(request->arguments[0], request->arguments, request->environment);
        report_error("cannot execute brokered process");
        _exit(errno == ENOENT ? 127 : 126);
    }

    workers[*worker_count] = (struct broker_worker){
        .pid = child,
        .owner_group = owner_group,
        .connection_fd = connection_fd,
        .leader_alive = true,
        .cancel_sent = false,
        .kill_deadline_ms = 0,
    };
    (*worker_count)++;
    return 0;
}

static int
handle_broker_connection(int listener_fd, const struct policy *policy,
                         pid_t host_pid, const char *socket_name, const char *launcher_path,
                         const char *scratch_path, int scratch_fd, int parent_fd,
                         struct broker_worker *workers, size_t *worker_count)
{
    int connection_fd = accept4(listener_fd, NULL, NULL, SOCK_CLOEXEC);
    if (connection_fd < 0) {
        return errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK ? 0 : -1;
    }
    struct ucred credentials;
    socklen_t credentials_length = sizeof(credentials);
    pid_t owner_group = 0;
    if (getsockopt(connection_fd, SOL_SOCKET, SO_PEERCRED, &credentials,
                   &credentials_length) < 0 ||
        credentials_length != sizeof(credentials) || credentials.uid != geteuid() ||
        !process_belongs_to_family(credentials.pid, host_pid, workers, *worker_count,
                                   &owner_group)) {
        (void)close(connection_fd);
        return 0;
    }
    if (set_connection_timeout(connection_fd) < 0) {
        (void)close(connection_fd);
        return 0;
    }

    struct broker_request_header header;
    int received_fds[2] = {-1, -1};
    if (receive_header_and_fds(connection_fd, &header, received_fds) < 0) {
        reject_broker_connection(connection_fd, received_fds);
        return 0;
    }
    struct broker_request request = {0};
    if (parse_broker_request(connection_fd, &header, socket_name, launcher_path,
                             scratch_path, &request) < 0) {
        free_broker_request(&request);
        reject_broker_connection(connection_fd, received_fds);
        return 0;
    }

    int cwd_fd = open(request.working_directory, O_PATH | O_DIRECTORY | O_CLOEXEC);
    struct stat cwd_status;
    if (cwd_fd < 0 || fstat(cwd_fd, &cwd_status) < 0 || !S_ISDIR(cwd_status.st_mode)) {
        if (cwd_fd >= 0) {
            (void)close(cwd_fd);
        }
        free_broker_request(&request);
        reject_broker_connection(connection_fd, received_fds);
        return 0;
    }
    if (spawn_broker_worker(policy, &request, received_fds, owner_group, connection_fd, listener_fd,
                            cwd_fd, scratch_fd, parent_fd, workers, worker_count) < 0) {
        (void)close(cwd_fd);
        free_broker_request(&request);
        reject_broker_connection(connection_fd, received_fds);
        return 0;
    }
    (void)close(cwd_fd);
    close_received_fds(received_fds, 2);
    free_broker_request(&request);
    return 0;
}

static void
remove_broker_worker(struct broker_worker *workers, size_t *worker_count, size_t index)
{
    if (workers[index].connection_fd >= 0) {
        (void)close(workers[index].connection_fd);
    }
    (*worker_count)--;
    if (index != *worker_count) {
        workers[index] = workers[*worker_count];
    }
}

static bool
process_group_exists(pid_t process_group)
{
    if (process_group <= 0) {
        return false;
    }
    if (kill(-process_group, 0) == 0 || errno == EPERM) {
        return true;
    }
    return false;
}

static void
prune_broker_workers(struct broker_worker *workers, size_t *worker_count)
{
    for (size_t index = 0; index < *worker_count;) {
        if (!workers[index].leader_alive && !process_group_exists(workers[index].pid)) {
            remove_broker_worker(workers, worker_count, index);
        } else {
            index++;
        }
    }
}

static void
reap_broker_children(pid_t host_pid, bool *host_done, int *host_status,
                     struct broker_worker *workers, size_t *worker_count)
{
    for (;;) {
        int status;
        pid_t child = waitpid(-1, &status, WNOHANG);
        if (child <= 0) {
            break;
        }
        if (child == host_pid) {
            *host_done = true;
            *host_status = status;
            continue;
        }
        for (size_t index = 0; index < *worker_count; index++) {
            if (workers[index].pid == child) {
                uint32_t kind = WIFEXITED(status) ? BROKER_RESPONSE_EXIT : BROKER_RESPONSE_SIGNAL;
                uint32_t value = WIFEXITED(status) ? (uint32_t)WEXITSTATUS(status) :
                                                     (uint32_t)WTERMSIG(status);
                if (workers[index].connection_fd >= 0) {
                    (void)send_broker_response(workers[index].connection_fd, kind, value);
                    (void)close(workers[index].connection_fd);
                    workers[index].connection_fd = -1;
                }
                workers[index].leader_alive = false;
                break;
            }
        }
    }
    prune_broker_workers(workers, worker_count);
}

static void
cancel_broker_worker(struct broker_worker *worker, int64_t now)
{
    if (worker->cancel_sent) {
        return;
    }
    if (worker->pid > 0 && kill(-worker->pid, SIGTERM) < 0 && worker->leader_alive) {
        (void)kill(worker->pid, SIGTERM);
    }
    worker->cancel_sent = true;
    worker->kill_deadline_ms = now < 0 ? 0 : now + 500;
    if (worker->connection_fd >= 0) {
        (void)close(worker->connection_fd);
        worker->connection_fd = -1;
    }
}

static void
cancel_broker_worker_descendants(struct broker_worker *workers, size_t worker_count,
                                pid_t parent_group, int64_t now)
{
    pid_t pending[BROKER_WORKER_LIMIT + 1];
    size_t pending_count = 1;
    pending[0] = parent_group;
    for (size_t cursor = 0; cursor < pending_count; cursor++) {
        for (size_t index = 0; index < worker_count; index++) {
            if (workers[index].owner_group == pending[cursor] &&
                !workers[index].cancel_sent) {
                pid_t child_group = workers[index].pid;
                cancel_broker_worker(&workers[index], now);
                if (pending_count < BROKER_WORKER_LIMIT + 1) {
                    pending[pending_count++] = child_group;
                }
            }
        }
    }
}

static int
supervise_host(pid_t host_pid, int listener_fd, const struct policy *policy,
               const char *socket_name, const char *launcher_path, const char *scratch_path,
               int scratch_fd, int parent_fd, int *host_status)
{
    struct broker_worker *workers = calloc(BROKER_WORKER_LIMIT, sizeof(*workers));
    if (workers == NULL) {
        (void)kill(-host_pid, SIGTERM);
        (void)kill(host_pid, SIGTERM);
        int ignored_status;
        (void)waitpid(host_pid, &ignored_status, 0);
        return -1;
    }
    struct pollfd poll_fds[BROKER_WORKER_LIMIT + 1];
    size_t worker_poll_indices[BROKER_WORKER_LIMIT];
    size_t worker_count = 0;
    bool host_done = false;
    bool shutting_down = false;
    int64_t host_kill_deadline_ms = 0;
    int result = 0;

    while (!host_done || process_group_exists(host_pid) || worker_count > 0) {
        reap_broker_children(host_pid, &host_done, host_status, workers, &worker_count);
        bool host_failed = host_done &&
                           (!WIFEXITED(*host_status) || WEXITSTATUS(*host_status) != 0);
        if (host_failed && !shutting_down) {
            shutting_down = true;
            if (listener_fd >= 0) {
                (void)close(listener_fd);
                listener_fd = -1;
            }
            int64_t now = monotonic_milliseconds();
            (void)kill(-host_pid, SIGTERM);
            host_kill_deadline_ms = now < 0 ? 0 : now + 500;
            for (size_t index = 0; index < worker_count; index++) {
                cancel_broker_worker(&workers[index], now);
            }
        }

        int64_t now = monotonic_milliseconds();
        if (shutting_down && host_kill_deadline_ms > 0 &&
            now >= host_kill_deadline_ms && process_group_exists(host_pid)) {
            (void)kill(-host_pid, SIGKILL);
            host_kill_deadline_ms = 0;
        }
        for (size_t index = 0; index < worker_count; index++) {
            struct broker_worker *worker = &workers[index];
            if (worker->cancel_sent && worker->kill_deadline_ms > 0 &&
                now >= worker->kill_deadline_ms && worker->pid > 0) {
                (void)kill(-worker->pid, SIGKILL);
                if (worker->leader_alive) {
                    (void)kill(worker->pid, SIGKILL);
                }
                worker->kill_deadline_ms = 0;
            }
        }

        nfds_t poll_count = 0;
        bool poll_listener = listener_fd >= 0 && !shutting_down;
        if (poll_listener) {
            poll_fds[poll_count++] = (struct pollfd){.fd = listener_fd, .events = POLLIN};
        }
        size_t worker_poll_count = 0;
        for (size_t index = 0; index < worker_count; index++) {
            if (workers[index].connection_fd >= 0) {
                worker_poll_indices[worker_poll_count++] = index;
                poll_fds[poll_count++] = (struct pollfd){
                    .fd = workers[index].connection_fd,
                    .events = POLLIN,
                };
            }
        }
        int poll_result = poll(poll_fds, poll_count, 50);
        if (poll_result < 0 && errno != EINTR) {
            result = -1;
            shutting_down = true;
            if (listener_fd >= 0) {
                (void)close(listener_fd);
                listener_fd = -1;
            }
            now = monotonic_milliseconds();
            if (!host_done) {
                (void)kill(host_pid, SIGTERM);
            }
            (void)kill(-host_pid, SIGTERM);
            host_kill_deadline_ms = now < 0 ? 0 : now + 500;
            for (size_t index = 0; index < worker_count; index++) {
                cancel_broker_worker(&workers[index], now);
            }
        } else if (poll_result > 0) {
            nfds_t worker_poll_offset = poll_listener ? 1 : 0;
            for (size_t poll_index = 0; poll_index < worker_poll_count; poll_index++) {
                struct broker_worker *worker = &workers[worker_poll_indices[poll_index]];
                if (worker->connection_fd < 0) {
                    continue;
                }
                struct pollfd *worker_poll = &poll_fds[worker_poll_offset + poll_index];
                if (worker_poll->fd != worker->connection_fd ||
                    (worker_poll->revents & (POLLHUP | POLLERR | POLLNVAL))) {
                    int64_t cancel_time = monotonic_milliseconds();
                    pid_t disconnected_group = worker->pid;
                    cancel_broker_worker(worker, cancel_time);
                    cancel_broker_worker_descendants(workers, worker_count,
                                                      disconnected_group, cancel_time);
                    continue;
                }
                if (worker_poll->revents & POLLIN) {
                    char unexpected;
                    ssize_t count = recv(worker->connection_fd, &unexpected, sizeof(unexpected),
                                         MSG_PEEK | MSG_DONTWAIT);
                    if (count == 0 || count > 0 ||
                        (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)) {
                        int64_t cancel_time = monotonic_milliseconds();
                        pid_t disconnected_group = worker->pid;
                        cancel_broker_worker(worker, cancel_time);
                        cancel_broker_worker_descendants(workers, worker_count,
                                                          disconnected_group, cancel_time);
                    }
                }
            }
            if (poll_listener && (poll_fds[0].revents & POLLIN)) {
                if (handle_broker_connection(listener_fd, policy, host_pid, socket_name,
                                             launcher_path, scratch_path, scratch_fd,
                                             parent_fd, workers, &worker_count) < 0) {
                    result = -1;
                    shutting_down = true;
                    (void)close(listener_fd);
                    listener_fd = -1;
                    now = monotonic_milliseconds();
                    if (!host_done) {
                        (void)kill(host_pid, SIGTERM);
                    }
                    (void)kill(-host_pid, SIGTERM);
                    host_kill_deadline_ms = now < 0 ? 0 : now + 500;
                    for (size_t index = 0; index < worker_count; index++) {
                        cancel_broker_worker(&workers[index], now);
                    }
                }
            }
        }
        reap_broker_children(host_pid, &host_done, host_status, workers, &worker_count);
    }

    if (listener_fd >= 0) {
        (void)close(listener_fd);
    }
    free(workers);
    return result;
}

static void
close_child_descriptors(void)
{
#ifdef __NR_close_range
    if (syscall(__NR_close_range, 3U, UINT_MAX, 0U) == 0) {
        return;
    }
#endif
    struct rlimit limit;
    if (getrlimit(RLIMIT_NOFILE, &limit) < 0) {
        limit.rlim_cur = 65536;
    }
    rlim_t maximum = limit.rlim_cur;
    if (maximum == RLIM_INFINITY || maximum > 1048576) {
        maximum = 1048576;
    }
    for (int fd = 3; (rlim_t)fd < maximum; fd++) {
        (void)close(fd);
    }
}

static int
launch(const char *policy_path, char **program_argv)
{
    struct policy policy = {0};
    for (size_t index = 0; index < RULE_LIMIT; index++) {
        policy.rules[index].fd = -1;
    }
    if (parse_policy(policy_path, &policy) < 0) {
        close_policy(&policy);
        free(policy.scratch_parent);
        return 1;
    }

    int abi = landlock_abi();
    if (abi < 1) {
        if (errno == ENOSYS || errno == EOPNOTSUPP || errno == EINVAL) {
            (void)fprintf(stderr, "genbuild-landlock-launcher: Landlock is unsupported by this kernel\n");
        } else {
            report_error("cannot query Landlock ABI");
        }
        close_policy(&policy);
        free(policy.scratch_parent);
        return 78;
    }

    if (getenv(STAGING_ENV) != NULL || getenv(BROKER_SOCKET_ENV) != NULL ||
        getenv(BROKER_LAUNCHER_ENV) != NULL) {
        errno = EINVAL;
        report_error("reserved action-launcher environment variable is already set");
        close_policy(&policy);
        free(policy.scratch_parent);
        return 1;
    }

    int parent_fd = -1;
    int scratch_fd = -1;
    char *scratch_path = NULL;
    char *scratch_name = NULL;
    struct stat scratch_status;
    if (make_private_scratch(policy.scratch_parent, &parent_fd, &scratch_fd,
                             &scratch_path, &scratch_name, &scratch_status) < 0) {
        close_policy(&policy);
        free(policy.scratch_parent);
        return 1;
    }

    char *launcher_path = NULL;
    char *socket_name = NULL;
    int listener_fd = -1;
    if (read_launcher_path(&launcher_path) < 0 ||
        create_broker_listener(&listener_fd, &socket_name) < 0) {
        report_error("cannot initialize action broker");
        cleanup_scratch(parent_fd, scratch_fd, scratch_name, &scratch_status);
        close_policy(&policy);
        (void)close(scratch_fd);
        (void)close(parent_fd);
        free(scratch_path);
        free(scratch_name);
        free(launcher_path);
        free(socket_name);
        free(policy.scratch_parent);
        return 1;
    }

    pid_t child = fork();
    if (child < 0) {
        report_error("cannot fork host process");
        (void)close(listener_fd);
        cleanup_scratch(parent_fd, scratch_fd, scratch_name, &scratch_status);
        close_policy(&policy);
        (void)close(scratch_fd);
        (void)close(parent_fd);
        free(scratch_path);
        free(scratch_name);
        free(launcher_path);
        free(socket_name);
        free(policy.scratch_parent);
        return 1;
    }
    if (child == 0) {
        if (setpgid(0, 0) < 0) {
            report_error("cannot isolate host process group");
            _exit(126);
        }
        int null_fd = open("/dev/null", O_RDONLY | O_CLOEXEC);
        if (null_fd < 0 || dup2(null_fd, STDIN_FILENO) < 0) {
            report_error("cannot detach standard input");
            _exit(126);
        }
        if (null_fd != STDIN_FILENO) {
            (void)close(null_fd);
        }

        int self_fds[SELF_RULE_LIMIT];
        if (open_self_rule_fds(&policy, self_fds) < 0) {
            _exit(126);
        }
        int ruleset_fd = -1;
        if (add_landlock_rules(&policy, self_fds, scratch_fd, &ruleset_fd) < 0) {
            close_self_rule_fds(self_fds);
            _exit(126);
        }
        if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) < 0) {
            report_error("cannot set no_new_privs");
            _exit(126);
        }
        if (syscall(__NR_landlock_restrict_self, ruleset_fd, 0) < 0) {
            report_error("cannot enforce Landlock ruleset");
            _exit(126);
        }
        if (setenv(STAGING_ENV, scratch_path, 1) < 0 ||
            setenv(BROKER_SOCKET_ENV, socket_name, 1) < 0 ||
            setenv(BROKER_LAUNCHER_ENV, launcher_path, 1) < 0 ||
            setenv(TMPDIR_ENV, scratch_path, 1) < 0 ||
            setenv(TMP_ENV, scratch_path, 1) < 0 ||
            setenv(TEMP_ENV, scratch_path, 1) < 0) {
            report_error("cannot set action host environment");
            _exit(126);
        }
        (void)close(ruleset_fd);
        close_self_rule_fds(self_fds);
        close_policy(&policy);
        (void)close(scratch_fd);
        (void)close(parent_fd);
        close_child_descriptors();
        execvp(program_argv[0], program_argv);
        report_error("cannot execute host program");
        _exit(errno == ENOENT ? 127 : 126);
    }

    int child_status = 0;
    int supervision_result = supervise_host(child, listener_fd, &policy, socket_name,
                                            launcher_path, scratch_path, scratch_fd,
                                            parent_fd, &child_status);

    cleanup_scratch(parent_fd, scratch_fd, scratch_name, &scratch_status);
    (void)close(scratch_fd);
    (void)close(parent_fd);
    close_policy(&policy);
    free(scratch_path);
    free(scratch_name);
    free(launcher_path);
    free(socket_name);
    free(policy.scratch_parent);

    if (supervision_result < 0) {
        report_error("action broker supervision failed");
        return 1;
    }
    if (!WIFEXITED(child_status) && !WIFSIGNALED(child_status)) {
        return 1;
    }
    if (WIFEXITED(child_status)) {
        return WEXITSTATUS(child_status);
    }
    if (WIFSIGNALED(child_status)) {
        int signal_number = WTERMSIG(child_status);
        (void)signal(signal_number, SIG_DFL);
        (void)raise(signal_number);
        return 128 + signal_number;
    }
    return 1;
}

int
main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--probe") == 0) {
        int abi = landlock_abi();
        if (abi < 1) {
            if (errno == ENOSYS || errno == EOPNOTSUPP || errno == EINVAL) {
                (void)fprintf(stderr, "genbuild-landlock-launcher: Landlock is unsupported by this kernel\n");
                return 78;
            }
            report_error("cannot query Landlock ABI");
            return 1;
        }
        (void)printf("%d\n", abi);
        return 0;
    }
    if (argc >= 5 && strcmp(argv[1], "--broker") == 0 &&
        strcmp(argv[3], "--") == 0) {
        return broker_client(argv[2], &argv[4]);
    }
    if (argc < 5 || strcmp(argv[1], "--policy") != 0 ||
        argv[2][0] != '/' || strcmp(argv[3], "--") != 0 || argv[4][0] == '\0') {
        (void)fprintf(stderr,
                      "usage: genbuild-landlock --probe | --policy ABSFILE -- PROGRAM [args...] | "
                      "--broker SOCKET -- PROGRAM [args...]\n");
        return 2;
    }
    return launch(argv[2], &argv[4]);
}
