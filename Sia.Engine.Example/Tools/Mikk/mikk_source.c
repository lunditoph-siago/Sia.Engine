/* Offline adapter for the separately vendored reference MikkTSpace sources.
 * Input is a caller-authenticated canonical Source snapshot. Results are
 * unindexed corners; this tool never overwrites tangents through old indices.
 */
#include <float.h>
#include <limits.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "mikktspace.h"

#ifdef _WIN32
#include <wchar.h>
typedef wchar_t PathCharacter;
#define TOOL_MAIN wmain
#define TOOL_FOPEN _wfopen
#define TOOL_REMOVE _wremove
#define TOOL_READ_MODE L"rb"
#define TOOL_WRITE_MODE L"wbx"
#else
typedef char PathCharacter;
#define TOOL_MAIN main
#define TOOL_FOPEN fopen
#define TOOL_REMOVE remove
#define TOOL_READ_MODE "rb"
#define TOOL_WRITE_MODE "wbx"
#endif

_Static_assert(CHAR_BIT == 8 && sizeof(uint32_t) == 4, "8-bit bytes and uint32 are required");
_Static_assert(sizeof(float) == 4 && FLT_RADIX == 2 && FLT_MANT_DIG == 24 && FLT_MAX_EXP == 128,
               "IEEE binary32 floats are required");

typedef struct {
    uint32_t vertex_count, index_count, writes;
    float *vertices, *tangents;
    uint32_t *indices;
    unsigned char *written;
    int invalid_callback;
} Input;

static uint32_t u32(const unsigned char *bytes) {
    return (uint32_t)bytes[0] | (uint32_t)bytes[1] << 8 |
           (uint32_t)bytes[2] << 16 | (uint32_t)bytes[3] << 24;
}
static void put_u32(unsigned char *bytes, uint32_t value) {
    for (unsigned int i = 0; i < 4; ++i) bytes[i] = (unsigned char)(value >> (i * 8));
}
static float f32(const unsigned char *bytes) {
    uint32_t bits = u32(bytes);
    float result;
    memcpy(&result, &bits, sizeof(result));
    return result;
}
static int platform_supported(void) {
    const uint32_t word = 1;
    const float one = 1.0f;
    uint32_t bits;
    memcpy(&bits, &one, sizeof(bits));
    return *(const unsigned char *)&word == 1 && bits == UINT32_C(0x3f800000);
}
static int parse_limit(const PathCharacter *text, uint64_t *value) {
    uint64_t result = 0;
    if (*text == '\0') return 0;
    for (; *text != '\0'; ++text) {
        if (*text < '0' || *text > '9') return 0;
        const unsigned int digit = (unsigned int)(*text - '0');
        if (result > (UINT64_MAX - digit) / 10) return 0;
        result = result * 10 + digit;
    }
    *value = result;
    return result != 0;
}
static int admit(uint64_t *total, uint64_t count, uint64_t stride, uint64_t limit) {
    if (count != 0 && stride > UINT64_MAX / count) return 0;
    const uint64_t bytes = count * stride;
    if (*total > limit || bytes > limit - *total) return 0;
    *total += bytes;
    return 1;
}
static int array_bytes(uint32_t count, size_t stride, uint64_t limit, size_t *bytes) {
    if ((uint64_t)count > (uint64_t)SIZE_MAX / stride || (uint64_t)count > limit / stride) return 0;
    *bytes = (size_t)count * stride;
    return 1;
}
static int read_exact(FILE *file, void *target, size_t bytes) {
    return fread(target, 1, bytes, file) == bytes;
}
static Input *data(const SMikkTSpaceContext *context) { return (Input *)context->m_pUserData; }
static const float *vertex(const SMikkTSpaceContext *context, int face, int corner) {
    static const float invalid[12] = {0};
    Input *input = data(context);
    if (face < 0 || (uint32_t)face >= input->index_count / 3 || corner < 0 || corner >= 3) {
        input->invalid_callback = 1;
        return invalid;
    }
    const uint32_t index = input->indices[(size_t)(uint32_t)face * 3 + (unsigned int)corner];
    return input->vertices + (size_t)index * 12;
}
static int faces(const SMikkTSpaceContext *context) { return (int)(data(context)->index_count / 3); }
static int corners(const SMikkTSpaceContext *context, int face) { (void)context; (void)face; return 3; }
static void position(const SMikkTSpaceContext *context, float output[], int face, int corner) {
    memcpy(output, vertex(context, face, corner), 3 * sizeof(float));
}
static void normal(const SMikkTSpaceContext *context, float output[], int face, int corner) {
    memcpy(output, vertex(context, face, corner) + 3, 3 * sizeof(float));
}
static void uv(const SMikkTSpaceContext *context, float output[], int face, int corner) {
    memcpy(output, vertex(context, face, corner) + 6, 2 * sizeof(float));
}
static void tangent(const SMikkTSpaceContext *context, const float value[], float sign, int face, int corner) {
    Input *input = data(context);
    if (face < 0 || (uint32_t)face >= input->index_count / 3 || corner < 0 || corner >= 3 ||
        !isfinite(value[0]) || !isfinite(value[1]) || !isfinite(value[2]) || (sign != 1.0f && sign != -1.0f)) {
        input->invalid_callback = 1;
        return;
    }
    const size_t offset = (size_t)(uint32_t)face * 3 + (unsigned int)corner;
    if (input->written[offset]) { input->invalid_callback = 1; return; }
    float *output = input->tangents + offset * 4;
    memcpy(output, value, 3 * sizeof(float)); output[3] = sign;
    input->written[offset] = 1; ++input->writes;
}

int TOOL_MAIN(int argc, PathCharacter **argv) {
    Input input = {0};
    FILE *source = NULL, *output = NULL;
    int result = 1, created_output = 0;
    const char *error = "Mikk source conversion failed.\n";
    uint64_t limit;
    unsigned char header[77];
    size_t vertex_bytes, index_bytes, tangent_bytes, written_bytes;
    if (argc != 4 || !parse_limit(argv[3], &limit)) {
        fputs("usage: mikk_source source.siaasset new-output.bin maximumBytes\n", stderr);
        return 2;
    }
    if (!platform_supported()) { fputs("Little-endian IEEE binary32 host required.\n", stderr); return 3; }
    source = TOOL_FOPEN(argv[1], TOOL_READ_MODE);
    if (source == NULL) { error = "Cannot open Source snapshot.\n"; result = 4; goto cleanup; }
    if (limit < sizeof(header) || !read_exact(source, header, sizeof(header)) || memcmp(header, "SIAMSHS\0", 8) != 0) {
        error = "Invalid or truncated Source header.\n"; result = 5; goto cleanup;
    }
    input.vertex_count = u32(header + 8); input.index_count = u32(header + 12);
    const uint32_t sections = u32(header + 16);
    const int has_bake = header[20];
    /* Mikk packs a face index and two corner bits into signed int. */
    if (input.vertex_count == 0 || input.vertex_count > INT_MAX || input.index_count == 0 ||
        input.index_count > INT_MAX || input.index_count % 3 != 0 || input.index_count / 3 > INT_MAX / 4 ||
        sections == 0 || sections > input.index_count / 3 || has_bake > 1) {
        error = "Invalid Source counts or flags.\n"; result = 5; goto cleanup;
    }
    uint64_t wire = sizeof(header), owned = 0, encoded_result = 16;
    if (!admit(&wire, input.vertex_count, 48, limit) || !admit(&wire, input.index_count, 4, limit) ||
        !admit(&wire, sections, 12, limit) || !admit(&wire, has_bake ? input.vertex_count : 0, 8, limit) ||
        !admit(&owned, input.vertex_count, 64, limit) || !admit(&owned, input.index_count, 4, limit) ||
        !admit(&owned, sections, 12, limit) || !admit(&owned, has_bake ? input.vertex_count : 0, 16, limit) ||
        !admit(&encoded_result, input.index_count, 16, limit) ||
        !array_bytes(input.vertex_count, 48, limit, &vertex_bytes) || !array_bytes(input.index_count, 4, limit, &index_bytes) ||
        !array_bytes(input.index_count, 16, limit, &tangent_bytes) || !array_bytes(input.index_count, 1, limit, &written_bytes)) {
        error = "Source, adapter array or result exceeds byte admission.\n"; result = 6; goto cleanup;
    }
    float bounds[6];
    for (unsigned int i = 0; i < 6; ++i) {
        bounds[i] = f32(header + 21 + i * 4);
        if (!isfinite(bounds[i])) { error = "Nonfinite Source bounds.\n"; result = 5; goto cleanup; }
    }
    for (unsigned int i = 0; i < 3; ++i) if (bounds[i] > bounds[i + 3]) {
        error = "Unordered Source bounds.\n"; result = 5; goto cleanup;
    }
    input.vertices = (float *)malloc(vertex_bytes);
    input.indices = (uint32_t *)malloc(index_bytes);
    input.tangents = (float *)malloc(tangent_bytes);
    input.written = (unsigned char *)calloc(written_bytes, 1);
    if (input.vertices == NULL || input.indices == NULL || input.tangents == NULL || input.written == NULL) {
        error = "Adapter allocation failed.\n"; result = 7; goto cleanup;
    }
    if (!read_exact(source, input.vertices, vertex_bytes) || !read_exact(source, input.indices, index_bytes)) {
        error = "Truncated Source attributes or indices.\n"; result = 8; goto cleanup;
    }
    for (uint32_t i = 0; i < input.vertex_count; ++i) {
        const float *v = input.vertices + (size_t)i * 12;
        for (unsigned int component = 0; component < 12; ++component) if (!isfinite(v[component])) {
            error = "Nonfinite Source vertex attribute.\n"; result = 8; goto cleanup;
        }
        for (unsigned int axis = 0; axis < 3; ++axis) if (v[axis] < bounds[axis] || v[axis] > bounds[axis + 3]) {
            error = "Source position is outside its bounds.\n"; result = 8; goto cleanup;
        }
    }
    for (uint32_t i = 0; i < input.index_count; ++i) if (input.indices[i] >= input.vertex_count) {
        error = "Source index is outside its vertex array.\n"; result = 8; goto cleanup;
    }
    uint32_t cursor = 0;
    unsigned char row[12];
    for (uint32_t i = 0; i < sections; ++i) {
        if (!read_exact(source, row, sizeof(row))) { error = "Truncated Source section table.\n"; result = 8; goto cleanup; }
        const uint32_t first = u32(row), count = u32(row + 4), slot = u32(row + 8);
        if (first != cursor || count == 0 || count % 3 != 0 || count > input.index_count - cursor || slot > INT_MAX) {
            error = "Source sections do not partition the triangle list.\n"; result = 8; goto cleanup;
        }
        cursor += count;
    }
    if (cursor != input.index_count) { error = "Source sections do not cover every triangle.\n"; result = 8; goto cleanup; }
    if (has_bake) for (uint32_t i = 0; i < input.vertex_count; ++i) {
        if (!read_exact(source, row, 8) || !isfinite(f32(row)) || !isfinite(f32(row + 4))) {
            error = "Invalid or truncated Source bake UV.\n"; result = 8; goto cleanup;
        }
    }
    if (fgetc(source) != EOF || ferror(source)) { error = "Trailing or unreadable Source data.\n"; result = 8; goto cleanup; }
    { const int close_result = fclose(source); source = NULL;
      if (close_result != 0) { error = "Source snapshot close failed.\n"; result = 8; goto cleanup; } }

    SMikkTSpaceInterface api = {0};
    api.m_getNumFaces = faces; api.m_getNumVerticesOfFace = corners;
    api.m_getPosition = position; api.m_getNormal = normal; api.m_getTexCoord = uv; api.m_setTSpaceBasic = tangent;
    SMikkTSpaceContext context = {0}; context.m_pInterface = &api; context.m_pUserData = &input;
    if (!genTangSpaceDefault(&context) || input.invalid_callback || input.writes != input.index_count) {
        error = "Reference Mikk did not produce exactly one valid tangent per corner.\n"; result = 9; goto cleanup;
    }
    for (uint32_t i = 0; i < input.index_count; ++i) {
        const float *t = input.tangents + (size_t)i * 4;
        if (input.written[i] != 1 || !isfinite(t[0]) || !isfinite(t[1]) || !isfinite(t[2]) || (t[3] != 1.0f && t[3] != -1.0f)) {
            error = "Missing or invalid corner tangent.\n"; result = 9; goto cleanup;
        }
    }
    output = TOOL_FOPEN(argv[2], TOOL_WRITE_MODE);
    if (output == NULL) { error = "Cannot exclusively create new result.\n"; result = 10; goto cleanup; }
    created_output = 1;
    unsigned char result_header[16] = {0}; memcpy(result_header, "MIKKTG\0\0", 8); put_u32(result_header + 8, input.index_count);
    if (fwrite(result_header, 1, sizeof(result_header), output) != sizeof(result_header) ||
        fwrite(input.tangents, 1, tangent_bytes, output) != tangent_bytes || fflush(output) != 0) {
        error = "Corner result write failed.\n"; result = 11; goto cleanup;
    }
    { const int close_result = fclose(output); output = NULL;
      if (close_result != 0) { error = "Corner result close failed.\n"; result = 11; goto cleanup; } }
    created_output = 0; result = 0;
    printf("PASS corners=%u vertices=%u raw-normal\n", (unsigned int)input.index_count, (unsigned int)input.vertex_count);
cleanup:
    if (source != NULL) (void)fclose(source);
    if (output != NULL) (void)fclose(output);
    if (created_output && TOOL_REMOVE(argv[2]) != 0) {
        error = "Result failed and its partial file could not be removed.\n"; result = 12;
    }
    free(input.written); free(input.tangents); free(input.indices); free(input.vertices);
    if (result != 0) fputs(error, stderr);
    return result;
}
