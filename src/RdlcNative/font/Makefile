CC ?= cc
PKGS = fontconfig freetype2 harfbuzz-subset
CPPFLAGS += $(shell pkg-config --cflags $(PKGS))
CFLAGS ?= -O2 -g
CFLAGS += -std=c11 -D_POSIX_C_SOURCE=200809L -fPIC -Wall -Wextra -Werror
LDLIBS += $(shell pkg-config --libs $(PKGS)) -lm -pthread

.PHONY: all check check-managed clean
all: librdlc_native.so

librdlc_native.so: rdlc_font.c rdlc_font.h
	$(CC) $(CPPFLAGS) $(CFLAGS) -shared -Wl,-z,defs -o $@ rdlc_font.c $(LDLIBS)

font-native-test: font_native_test.c rdlc_font.c rdlc_font.h
	$(CC) $(CPPFLAGS) $(CFLAGS) -o $@ font_native_test.c rdlc_font.c $(LDLIBS)

check: font-native-test
	./font-native-test

check-managed: librdlc_native.so
	LD_LIBRARY_PATH="$(CURDIR)$${LD_LIBRARY_PATH:+:$$LD_LIBRARY_PATH}" dotnet run --project tests/ManagedTest.csproj --verbosity quiet

clean:
	rm -f librdlc_native.so font-native-test
