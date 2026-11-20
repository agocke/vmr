CC ?= cc
PYTHON ?= python3
OUT_DIR ?=
CFLAGS ?= -O2
STRICT_CFLAGS = -std=c11 -Wall -Wextra -Werror -Wformat=2 -Wshadow -Wstrict-prototypes -Wmissing-prototypes -Wpedantic

.PHONY: all test

ifeq ($(strip $(OUT_DIR)),)
$(error Set OUT_DIR to an absolute, private build directory)
endif

all: $(OUT_DIR)/genbuild-landlock $(OUT_DIR)/landlock-fixture

$(OUT_DIR):
	mkdir -p "$@"

$(OUT_DIR)/genbuild-landlock: launcher.c | $(OUT_DIR)
	$(CC) $(CFLAGS) $(STRICT_CFLAGS) -o "$@" "$<"

$(OUT_DIR)/landlock-fixture: tests/fixture.c | $(OUT_DIR)
	$(CC) $(CFLAGS) $(STRICT_CFLAGS) -static -o "$@" "$<"

test: all
	GENBUILD_LAUNCHER="$(OUT_DIR)/genbuild-landlock" \
	GENBUILD_FIXTURE="$(OUT_DIR)/landlock-fixture" \
	GENBUILD_TEST_ROOT="$(OUT_DIR)" \
	PYTHONDONTWRITEBYTECODE=1 \
	$(PYTHON) -m unittest discover -s tests -p test_launcher.py -v
