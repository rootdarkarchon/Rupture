# PhysX 4.1 applies dominance to CCD impulses, but still clips the dominant
# body's trajectory. Compile a narrowly patched build-tree copy; leave the
# pinned SDK checkout untouched. Equal-dominance (including headless) paths
# retain the original behavior.
set(rpt_ccd_source "${RPT_PHYSX_SOURCE_DIR}/source/lowlevel/software/src/PxsCCD.cpp")
file(SHA256 "${rpt_ccd_source}" rpt_ccd_hash)
if(NOT rpt_ccd_hash STREQUAL "7d6b903fbce8646cad61e1264ed714cdbde77f1d60dfdb0a418a495227f33dc7")
    message(FATAL_ERROR "Pinned PhysX CCD source changed; revalidate the dominance correction")
endif()
file(READ "${rpt_ccd_source}" rpt_ccd)
string(FIND "${rpt_ccd}" "bool PxsCCDPair::sweepAdvanceToToi(" rpt_start)
string(FIND "${rpt_ccd}" "struct IslandCompare" rpt_end)
if(rpt_start LESS 0 OR rpt_end LESS rpt_start)
    message(FATAL_ERROR "Pinned PhysX CCD dominance patch anchors changed")
endif()
math(EXPR rpt_length "${rpt_end} - ${rpt_start}")
string(SUBSTRING "${rpt_ccd}" ${rpt_start} ${rpt_length} rpt_function)
set(rpt_original "${rpt_function}")
foreach(rpt_index RANGE 0 1)
    string(REPLACE "if (atom${rpt_index} && !atom${rpt_index}->mCCD->mPassDone)"
        "if (atom${rpt_index} && mCm->getDominance${rpt_index}() > 0.f && !atom${rpt_index}->mCCD->mPassDone)"
        rpt_function "${rpt_function}")
    string(REPLACE "if (atom${rpt_index})\n\t\t{\n\t\t\tatom${rpt_index}->mCCD->mPassDone"
        "if (atom${rpt_index} && mCm->getDominance${rpt_index}() > 0.f)\n\t\t{\n\t\t\tatom${rpt_index}->mCCD->mPassDone"
        rpt_function "${rpt_function}")
endforeach()
string(REPLACE "${rpt_original}" "${rpt_function}" rpt_ccd "${rpt_ccd}")
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/PxsCCD.casing.cpp" "${rpt_ccd}")
get_target_property(rpt_sources LowLevel SOURCES)
list(REMOVE_ITEM rpt_sources "${rpt_ccd_source}")
list(APPEND rpt_sources "${CMAKE_CURRENT_BINARY_DIR}/PxsCCD.casing.cpp")
set_property(TARGET LowLevel PROPERTY SOURCES "${rpt_sources}")
