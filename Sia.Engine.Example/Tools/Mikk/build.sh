#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ]; then
    printf '%s\n' 'usage: build.sh /absolute/build/output/directory' >&2
    exit 2
fi
case "$1" in
    /*) output=$1 ;;
    *) printf '%s\n' 'Choose an absolute build output directory.' >&2; exit 2 ;;
esac
source_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
repository_directory=$(CDPATH= cd -- "$source_directory/../../.." && pwd -P)
case "$output" in
    "$repository_directory"|"$repository_directory"/*)
        printf '%s\n' 'Choose build output outside the Engine checkout.' >&2; exit 2 ;;
esac
mkdir -p -- "$output"
output=$(CDPATH= cd -- "$output" && pwd -P)
case "$output" in
    "$repository_directory"|"$repository_directory"/*)
        printf '%s\n' 'Build output resolves inside the Engine checkout.' >&2; exit 2 ;;
esac
compiler=${CC:-cc}
version=$("$compiler" --version)
case "$version" in
    *[Cc]lang*) fp_flags='-fno-fast-math -ffp-contract=off -ffp-model=strict' ;;
    *[Gg][Cc][Cc]*|*'Free Software Foundation'*) fp_flags='-fno-fast-math -ffp-contract=off -fexcess-precision=standard -frounding-math' ;;
    *) printf '%s\n' 'Supported C compilers are Clang and GCC; strict floating-point flags must be known.' >&2; exit 2 ;;
esac
cd -- "$output"
# fp_flags contains only the fixed compiler flags above; source/output paths are
# separate quoted arguments. CC names one compiler executable, not shell code.
"$compiler" -std=c11 -O2 $fp_flags -Wall -Wextra -Wpedantic \
    -I"$source_directory/vendor" "$source_directory/mikk_source.c" \
    "$source_directory/vendor/mikktspace.c" -lm -o "$output/mikk_source"
printf '%s\n' "$output/mikk_source"
