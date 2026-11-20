#define _GNU_SOURCE

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <arpa/inet.h>
#include <signal.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char **environ;

#define BROKER_MAGIC UINT32_C(0x47424c31)
#define BROKER_RESPONSE_MAGIC UINT32_C(0x47425231)
#define BROKER_RESPONSE_ERROR 3U
#define BROKER_VERSION 1U
#define BROKER_MAX_REQUEST (2U * 1024U * 1024U)
#define BROKER_SOCKET_ENV "MSBUILD_ACTION_BROKER_SOCKET"
#define BROKER_LAUNCHER_ENV "MSBUILD_ACTION_BROKER_LAUNCHER"
#define STAGING_ENV "MSBUILD_ACTION_STAGING_ROOT"
#define TMPDIR_ENV "TMPDIR"

struct raw_broker_header {
    uint32_t magic;
    uint32_t version;
    uint32_t argc;
    uint32_t envc;
    uint32_t cwd_length;
    uint32_t payload_length;
};

struct raw_broker_response {
    uint32_t magic;
    uint32_t kind;
    uint32_t value;
};

_Static_assert(sizeof(struct raw_broker_header) == 24, "raw broker header size");
_Static_assert(sizeof(struct raw_broker_response) == 12, "raw broker response size");

static int
read_known_file(const char *path)
{
    char content[32];
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        return 2;
    }
    ssize_t count = read(fd, content, sizeof(content));
    (void)close(fd);
    if (count != 12 || memcmp(content, "known-input\n", 12) != 0) {
        return 3;
    }
    return 0;
}

static int
expect_read_denied(const char *path)
{
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd >= 0) {
        (void)close(fd);
        return 4;
    }
    return errno == EACCES || errno == EPERM ? 0 : 5;
}

static int
test_directory_listing_without_file_read(const char *directory_path, const char *file_path)
{
    DIR *directory = opendir(directory_path);
    if (directory == NULL) {
        return 6;
    }
    bool found_entry = false;
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") != 0 && strcmp(entry->d_name, "..") != 0) {
            found_entry = true;
        }
    }
    (void)closedir(directory);
    if (!found_entry) {
        return 7;
    }
    return expect_read_denied(file_path);
}

static int
test_descendant_denial(const char *program, const char *path)
{
    pid_t child = fork();
    if (child < 0) {
        return 8;
    }
    if (child == 0) {
        char *arguments[] = {(char *)program, "read-denied", (char *)path, NULL};
        execv(program, arguments);
        _exit(9);
    }
    int status;
    while (waitpid(child, &status, 0) < 0) {
        if (errno != EINTR) {
            return 10;
        }
    }
    return WIFEXITED(status) && WEXITSTATUS(status) == 0 ? 0 : 11;
}

static int
test_closed_descriptor(const char *value)
{
    char *end = NULL;
    long descriptor = strtol(value, &end, 10);
    if (end == value || *end != '\0' || descriptor < 3 || descriptor > 1048576) {
        return 12;
    }
    errno = 0;
    return fcntl((int)descriptor, F_GETFD) < 0 && errno == EBADF ? 0 : 13;
}

static int
test_detached_stdin(void)
{
    char value;
    ssize_t count = read(STDIN_FILENO, &value, sizeof(value));
    return count == 0 ? 0 : 14;
}

static int
test_private_temp_environment(void)
{
    const char *root = getenv("MSBUILD_ACTION_STAGING_ROOT");
    const char *variables[] = {"TMPDIR", "TMP", "TEMP"};
    if (root == NULL || root[0] != '/') {
        return 32;
    }
    for (size_t index = 0; index < sizeof(variables) / sizeof(variables[0]); index++) {
        const char *value = getenv(variables[index]);
        if (value == NULL || strcmp(value, root) != 0) {
            return 33;
        }
    }

    char probe_path[4096];
    if (snprintf(probe_path, sizeof(probe_path), "%s/CASESENSITIVETESTXXXXXX", root) >=
        (int)sizeof(probe_path)) {
        return 34;
    }
    int fd = mkstemp(probe_path);
    if (fd < 0) {
        return 35;
    }
    if (write(fd, "probe", 5) != 5) {
        (void)close(fd);
        (void)unlink(probe_path);
        return 36;
    }
    (void)close(fd);
    fd = open(probe_path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        (void)unlink(probe_path);
        return 37;
    }
    char content[5];
    ssize_t count = read(fd, content, sizeof(content));
    (void)close(fd);
    if (count != 5 || memcmp(content, "probe", 5) != 0) {
        (void)unlink(probe_path);
        return 38;
    }
    if (unlink(probe_path) < 0) {
        return 39;
    }

    DIR *root_directory = opendir(root);
    if (root_directory == NULL) {
        return 40;
    }
    bool root_is_empty = true;
    struct dirent *entry;
    while ((entry = readdir(root_directory)) != NULL) {
        if (strcmp(entry->d_name, ".") != 0 && strcmp(entry->d_name, "..") != 0) {
            root_is_empty = false;
        }
    }
    (void)closedir(root_directory);
    return root_is_empty ? 0 : 41;
}

static int
read_one_byte(const char *path)
{
    char value;
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        return -1;
    }
    ssize_t count = read(fd, &value, sizeof(value));
    (void)close(fd);
    return count == 1 ? 0 : -1;
}

static int
test_process_local_maps(const char *expected_root, bool expect_staging_root)
{
    const char *root = getenv(TMPDIR_ENV);
    const char *tmp = getenv("TMP");
    const char *temp = getenv("TEMP");
    const char *staging = getenv(STAGING_ENV);
    if (root == NULL || tmp == NULL || temp == NULL ||
        strcmp(root, expected_root) != 0 || strcmp(tmp, expected_root) != 0 ||
        strcmp(temp, expected_root) != 0 ||
        (expect_staging_root ? staging == NULL || strcmp(staging, expected_root) != 0
                             : staging != NULL)) {
        return 42;
    }
    if (read_one_byte("/proc/self/maps") < 0) {
        return 43;
    }
    char other_maps[128];
    if (snprintf(other_maps, sizeof(other_maps), "/proc/%ld/maps", (long)getppid()) >=
        (int)sizeof(other_maps)) {
        return 44;
    }
    int fd = open(other_maps, O_RDONLY | O_CLOEXEC);
    if (fd >= 0) {
        (void)close(fd);
        return 45;
    }
    return errno == EACCES || errno == EPERM ? 0 : 46;
}

static int
broker_spawn(const char *program, char **worker_arguments, bool spoof_controls)
{
    const char *launcher = getenv(BROKER_LAUNCHER_ENV);
    const char *socket_name = getenv(BROKER_SOCKET_ENV);
    const char *root = getenv(TMPDIR_ENV);
    if (launcher == NULL || launcher[0] != '/' || socket_name == NULL || root == NULL) {
        return 47;
    }
    size_t argument_count = 0;
    while (worker_arguments[argument_count] != NULL) {
        if (++argument_count > 100) {
            return 48;
        }
    }
    char *command[108];
    size_t index = 0;
    command[index++] = (char *)launcher;
    command[index++] = "--broker";
    command[index++] = (char *)socket_name;
    command[index++] = "--";
    command[index++] = (char *)program;
    for (size_t argument = 0; argument < argument_count; argument++) {
        command[index++] = worker_arguments[argument];
    }
    command[index] = NULL;

    pid_t child = fork();
    if (child < 0) {
        return 49;
    }
    if (child == 0) {
        if (spoof_controls &&
            (setenv(STAGING_ENV, "/spoof/root", 1) < 0 ||
             setenv(BROKER_SOCKET_ENV, "@spoof", 1) < 0 ||
             setenv(BROKER_LAUNCHER_ENV, "/spoof/launcher", 1) < 0 ||
             setenv("TMPDIR", "/spoof/tmp", 1) < 0 ||
             setenv("TMP", "/spoof/tmp", 1) < 0 ||
             setenv("TEMP", "/spoof/tmp", 1) < 0)) {
            _exit(50);
        }
        execv(launcher, command);
        _exit(51);
    }
    int status;
    while (waitpid(child, &status, 0) < 0) {
        if (errno != EINTR) {
            return 51;
        }
    }
    if (WIFEXITED(status)) {
        return WEXITSTATUS(status);
    }
    return WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 52;
}

static int
test_raw_broker_request(const char *kind)
{
    const char *socket_name = getenv(BROKER_SOCKET_ENV);
    if (socket_name == NULL || socket_name[0] != '@') {
        return 53;
    }
    int connection = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (connection < 0) {
        return 54;
    }
    struct sockaddr_un address = {.sun_family = AF_UNIX};
    size_t name_length = strlen(socket_name);
    if (name_length >= sizeof(address.sun_path)) {
        (void)close(connection);
        return 55;
    }
    address.sun_path[0] = '\0';
    memcpy(address.sun_path + 1, socket_name + 1, name_length - 1);
    socklen_t address_length = (socklen_t)(offsetof(struct sockaddr_un, sun_path) + name_length);
    if (connect(connection, (struct sockaddr *)&address, address_length) < 0) {
        (void)close(connection);
        return 56;
    }

    struct raw_broker_header header = {
        .magic = htonl(BROKER_MAGIC),
        .version = htonl(BROKER_VERSION),
        .argc = htonl(1),
        .envc = htonl(0),
        .cwd_length = htonl(2),
        .payload_length = htonl(2),
    };
    if (strcmp(kind, "bad-magic") == 0) {
        header.magic = htonl(0);
    } else if (strcmp(kind, "oversize") == 0) {
        header.payload_length = htonl(BROKER_MAX_REQUEST + 1U);
    } else if (strcmp(kind, "truncated") == 0) {
        header.payload_length = htonl(2);
    } else if (strcmp(kind, "extra-fds") != 0) {
        (void)close(connection);
        return 57;
    }

    int descriptors[3] = {STDOUT_FILENO, STDERR_FILENO, STDERR_FILENO};
    size_t descriptor_count = strcmp(kind, "extra-fds") == 0 ? 3 : 2;
    char control[CMSG_SPACE(sizeof(int) * 3)] = {0};
    struct iovec vector = {.iov_base = &header, .iov_len = sizeof(header)};
    struct msghdr message = {
        .msg_iov = &vector,
        .msg_iovlen = 1,
        .msg_control = control,
        .msg_controllen = CMSG_SPACE(sizeof(int) * descriptor_count),
    };
    struct cmsghdr *control_header = CMSG_FIRSTHDR(&message);
    control_header->cmsg_level = SOL_SOCKET;
    control_header->cmsg_type = SCM_RIGHTS;
    control_header->cmsg_len = CMSG_LEN(sizeof(int) * descriptor_count);
    memcpy(CMSG_DATA(control_header), descriptors, sizeof(int) * descriptor_count);
    ssize_t sent = sendmsg(connection, &message, MSG_NOSIGNAL);
    if (sent != (ssize_t)sizeof(header)) {
        (void)close(connection);
        return 58;
    }
    if (strcmp(kind, "truncated") == 0) {
        char partial = '\0';
        if (send(connection, &partial, 1, MSG_NOSIGNAL) != 1 ||
            shutdown(connection, SHUT_WR) < 0) {
            (void)close(connection);
            return 59;
        }
    }
    struct raw_broker_response response;
    size_t received = 0;
    while (received < sizeof(response)) {
        ssize_t count = recv(connection, (char *)&response + received,
                             sizeof(response) - received, 0);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            (void)close(connection);
            return 60;
        }
        received += (size_t)count;
    }
    (void)close(connection);
    return ntohl(response.magic) == BROKER_RESPONSE_MAGIC &&
           ntohl(response.kind) == BROKER_RESPONSE_ERROR ? 0 : 61;
}

static int
abandon_broker_client(const char *program)
{
    const char *launcher = getenv(BROKER_LAUNCHER_ENV);
    const char *socket_name = getenv(BROKER_SOCKET_ENV);
    if (launcher == NULL || socket_name == NULL) {
        return 62;
    }
    int output_pipe[2];
    if (pipe(output_pipe) < 0) {
        return 63;
    }
    pid_t client = fork();
    if (client < 0) {
        (void)close(output_pipe[0]);
        (void)close(output_pipe[1]);
        return 64;
    }
    if (client == 0) {
        (void)close(output_pipe[0]);
        if (dup2(output_pipe[1], STDOUT_FILENO) < 0) {
            _exit(65);
        }
        (void)close(output_pipe[1]);
        char *arguments[] = {(char *)launcher, "--broker", (char *)socket_name, "--",
                             (char *)program, "worker-ready", NULL};
        execv(launcher, arguments);
        _exit(66);
    }
    (void)close(output_pipe[1]);
    char line[64];
    size_t used = 0;
    while (used < sizeof(line) - 1) {
        ssize_t count = read(output_pipe[0], line + used, 1);
        if (count <= 0) {
            break;
        }
        if (line[used++] == '\n') {
            break;
        }
    }
    line[used] = '\0';
    bool ready = strcmp(line, "worker-ready\n") == 0;
    if (ready) {
        (void)kill(client, SIGKILL);
    }
    int status;
    while (waitpid(client, &status, 0) < 0) {
        if (errno != EINTR) {
            ready = false;
            break;
        }
    }
    (void)close(output_pipe[0]);
    return ready && WIFSIGNALED(status) && WTERMSIG(status) == SIGKILL ? 0 : 67;
}

static int
wait_for_release(const char *release_path)
{
    const char *socket_name = getenv(BROKER_SOCKET_ENV);
    if (socket_name == NULL) {
        return 68;
    }
    (void)puts(socket_name);
    (void)fflush(stdout);
    struct timespec delay = {.tv_sec = 0, .tv_nsec = 10000000};
    for (unsigned int attempt = 0; attempt < 1000; attempt++) {
        if (access(release_path, F_OK) == 0) {
            return 0;
        }
        (void)nanosleep(&delay, NULL);
    }
    return 69;
}

static int
test_outside_exec_denied(const char *path)
{
    pid_t child = fork();
    if (child < 0) {
        return 15;
    }
    if (child == 0) {
        char *arguments[] = {(char *)path, NULL};
        execve(path, arguments, environ);
        _exit(errno == EACCES || errno == EPERM ? 0 : 16);
    }
    int status;
    while (waitpid(child, &status, 0) < 0) {
        if (errno != EINTR) {
            return 17;
        }
    }
    return WIFEXITED(status) && WEXITSTATUS(status) == 0 ? 0 : 18;
}

static int
test_scratch(void)
{
    const char *root = getenv("MSBUILD_ACTION_STAGING_ROOT");
    if (root == NULL || root[0] != '/') {
        return 19;
    }
    struct stat status;
    if (stat(root, &status) < 0 || !S_ISDIR(status.st_mode) || (status.st_mode & 0777) != 0700) {
        return 20;
    }
    DIR *root_directory = opendir(root);
    if (root_directory == NULL) {
        return 30;
    }
    bool root_is_empty = true;
    struct dirent *entry;
    while ((entry = readdir(root_directory)) != NULL) {
        if (strcmp(entry->d_name, ".") != 0 && strcmp(entry->d_name, "..") != 0) {
            root_is_empty = false;
        }
    }
    (void)closedir(root_directory);
    if (!root_is_empty) {
        return 31;
    }
    char nested_directory[4096];
    char nested_file[4096];
    if (snprintf(nested_directory, sizeof(nested_directory), "%s/nested", root) >=
            (int)sizeof(nested_directory) ||
        snprintf(nested_file, sizeof(nested_file), "%s/nested/output", root) >=
            (int)sizeof(nested_file)) {
        return 21;
    }
    if (mkdir(nested_directory, 0700) < 0) {
        return 22;
    }
    int fd = open(nested_file, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
    if (fd < 0) {
        return 23;
    }
    if (write(fd, "scratch-data\n", 13) != 13) {
        (void)close(fd);
        return 24;
    }
    (void)close(fd);
    fd = open(nested_file, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        return 25;
    }
    char content[14] = {0};
    ssize_t count = read(fd, content, sizeof(content));
    (void)close(fd);
    if (count != 13 || memcmp(content, "scratch-data\n", 13) != 0) {
        return 26;
    }
    (void)puts(root);
    return 0;
}

static int
remove_scratch_root(void)
{
    const char *root = getenv("MSBUILD_ACTION_STAGING_ROOT");
    if (root == NULL || root[0] != '/') {
        return 28;
    }
    return rmdir(root) == 0 ? 0 : 29;
}

int
main(int argc, char **argv)
{
    if (argc < 2) {
        return 1;
    }
    if (strcmp(argv[1], "read") == 0 && argc == 3) {
        return read_known_file(argv[2]);
    }
    if (strcmp(argv[1], "read-one-byte") == 0 && argc == 3) {
        return read_one_byte(argv[2]) == 0 ? 0 : 79;
    }
    if (strcmp(argv[1], "read-denied") == 0 && argc == 3) {
        return expect_read_denied(argv[2]);
    }
    if (strcmp(argv[1], "directory-read-denied") == 0 && argc == 4) {
        return test_directory_listing_without_file_read(argv[2], argv[3]);
    }
    if (strcmp(argv[1], "descendant-denied") == 0 && argc == 3) {
        return test_descendant_denial(argv[0], argv[2]);
    }
    if (strcmp(argv[1], "closed-fd") == 0 && argc == 3) {
        return test_closed_descriptor(argv[2]);
    }
    if (strcmp(argv[1], "stdin") == 0 && argc == 2) {
        return test_detached_stdin();
    }
    if (strcmp(argv[1], "private-temp-env") == 0 && argc == 2) {
        return test_private_temp_environment();
    }
    if (strcmp(argv[1], "self-proc") == 0 && argc == 2) {
        return test_process_local_maps(getenv(TMPDIR_ENV), true);
    }
    if (strcmp(argv[1], "broker-proc") == 0 && argc == 3) {
        return test_process_local_maps(getenv(TMPDIR_ENV), false);
    }
    if (strcmp(argv[1], "remote-probe") == 0 && argc == 4) {
        char working_directory[65536];
        const char *broker_socket = getenv(BROKER_SOCKET_ENV);
        const char *broker_launcher = getenv(BROKER_LAUNCHER_ENV);
        if (getcwd(working_directory, sizeof(working_directory)) == NULL ||
            strcmp(working_directory, argv[2]) != 0 ||
            getenv(TMPDIR_ENV) == NULL ||
            strcmp(getenv(TMPDIR_ENV), getenv("TMP")) != 0 ||
            strcmp(getenv(TMPDIR_ENV), getenv("TEMP")) != 0 ||
            getenv(STAGING_ENV) != NULL ||
            getenv("GENBUILD_BROKER_TEST_VALUE") == NULL ||
            strcmp(getenv("GENBUILD_BROKER_TEST_VALUE"), argv[3]) != 0 ||
            broker_socket == NULL || strncmp(broker_socket, "@genbuild-", 10) != 0 ||
            broker_launcher == NULL || broker_launcher[0] != '/' ||
            access(broker_launcher, X_OK) < 0) {
            return 70;
        }
        char value;
        if (read(STDIN_FILENO, &value, sizeof(value)) != 0) {
            return 71;
        }
        (void)puts("broker-child-stdout");
        (void)fprintf(stderr, "broker-child-stderr\n");
        return 37;
    }
    if (strcmp(argv[1], "nested") == 0 && argc == 3) {
        char *arguments[] = {"broker-proc", argv[2], NULL};
        return broker_spawn(argv[0], arguments, false);
    }
    if (strcmp(argv[1], "broker-run") == 0 && argc >= 3) {
        if (strcmp(argv[2], "broker-proc") == 0 || strcmp(argv[2], "nested") == 0) {
            char *arguments[] = {argv[2], (char *)getenv(TMPDIR_ENV), NULL};
            return broker_spawn(argv[0], arguments, false);
        }
        return broker_spawn(argv[0], &argv[2], false);
    }
    if (strcmp(argv[1], "broker-run-spoof") == 0 && argc >= 3) {
        return broker_spawn(argv[0], &argv[2], true);
    }
    if (strcmp(argv[1], "broker-open-fd") == 0 && argc == 3) {
        int fd = open(argv[2], O_RDONLY);
        if (fd < 0) {
            return 72;
        }
        char descriptor[32];
        if (snprintf(descriptor, sizeof(descriptor), "%d", fd) >= (int)sizeof(descriptor)) {
            (void)close(fd);
            return 73;
        }
        char *arguments[] = {"closed-fd", descriptor, NULL};
        int result = broker_spawn(argv[0], arguments, false);
        (void)close(fd);
        return result;
    }
    if (strcmp(argv[1], "broker-raw") == 0 && argc == 3) {
        return test_raw_broker_request(argv[2]);
    }
    if (strcmp(argv[1], "abandon-client") == 0 && argc == 2) {
        return abandon_broker_client(argv[0]);
    }
    if (strcmp(argv[1], "worker-ready") == 0 && argc == 2) {
        (void)puts("worker-ready");
        (void)fflush(stdout);
        for (;;) {
            (void)pause();
        }
    }
    if (strcmp(argv[1], "wait-broker") == 0 && argc == 3) {
        return wait_for_release(argv[2]);
    }
    if (strcmp(argv[1], "exec-denied") == 0 && argc == 3) {
        return test_outside_exec_denied(argv[2]);
    }
    if (strcmp(argv[1], "scratch") == 0 && argc == 2) {
        return test_scratch();
    }
    if (strcmp(argv[1], "remove-scratch") == 0 && argc == 2) {
        return remove_scratch_root();
    }
    if (strcmp(argv[1], "exit") == 0 && argc == 2) {
        return 23;
    }
    if (strcmp(argv[1], "signal") == 0 && argc == 2) {
        (void)raise(SIGTERM);
        return 27;
    }
    return 1;
}
