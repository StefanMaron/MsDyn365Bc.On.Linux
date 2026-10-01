CC ?= cc
PKGS = pangocairo fontconfig freetype2 harfbuzz harfbuzz-subset
CPPFLAGS += $(shell pkg-config --cflags $(PKGS))
CFLAGS ?= -O2
CFLAGS += -std=c11 -D_POSIX_C_SOURCE=200809L -fPIC -Wall -Wextra -Werror
LDLIBS += $(shell pkg-config --libs $(PKGS)) -lm -pthread
OUTPUT ?= librdlc_native.so

.PHONY: all
all: $(OUTPUT)

$(OUTPUT): text.c rdlc_font.c rdlc_font.h
	$(CC) $(CPPFLAGS) $(CFLAGS) -shared -Wl,-z,defs -o $@ text.c rdlc_font.c $(LDLIBS)
