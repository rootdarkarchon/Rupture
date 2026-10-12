#include "rpt_physx_bridge.h"
#include "LzmaDec.h"
#include <cstdlib>

namespace
{
    void* allocate(ISzAllocPtr, size_t size) { return std::malloc(size); }
    void release(ISzAllocPtr, void* memory) { std::free(memory); }
}

int32_t RPTPX_CALL rptpx_lzma_decode_v1(const uint8_t* source, uint32_t source_size,
    uint8_t* destination, uint32_t destination_size)
{
    if (!source || source_size < LZMA_PROPS_SIZE || !destination || !destination_size)
        return RPTPX_INVALID_ARGUMENT;
    const ISzAlloc allocator{ allocate, release };
    SizeT consumed = source_size - LZMA_PROPS_SIZE;
    SizeT written = destination_size;
    ELzmaStatus status = LZMA_STATUS_NOT_SPECIFIED;
    const SRes result = LzmaDecode(destination, &written, source + LZMA_PROPS_SIZE, &consumed,
        source, LZMA_PROPS_SIZE, LZMA_FINISH_END, &status, &allocator);
    if (result == SZ_ERROR_MEM) return RPTPX_INTERNAL_ERROR;
    if (result != SZ_OK || written != destination_size || consumed != source_size - LZMA_PROPS_SIZE ||
        (status != LZMA_STATUS_FINISHED_WITH_MARK && status != LZMA_STATUS_MAYBE_FINISHED_WITHOUT_MARK))
        return RPTPX_INVALID_ARGUMENT;
    return RPTPX_OK;
}
