cmake_minimum_required(VERSION 3.16)
project(FamiStudioNesSndEmu LANGUAGES C CXX)

find_package(Threads REQUIRED)
add_library(NesSndEmu SHARED
    DllWrapper.cpp Simple_Apu.cpp
    nes_apu/apu_snapshot.cpp nes_apu/Blip_Buffer.cpp
    nes_apu/Nes_Apu.cpp nes_apu/Nes_Namco.cpp nes_apu/Nes_Oscs.cpp
    nes_apu/Nes_Vrc6.cpp nes_apu/Nes_Vrc7.cpp nes_apu/Nes_Fds.cpp
    nes_apu/Nes_Mmc5.cpp nes_apu/Nes_Sunsoft.cpp nes_apu/Nes_Fme7.cpp
    nes_apu/emu2413.c nes_apu/emu2149.c nes_apu/Nes_EPSM.cpp nes_apu/ym3438.cpp)
target_compile_features(NesSndEmu PRIVATE cxx_std_17)
target_include_directories(NesSndEmu PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(NesSndEmu PRIVATE Threads::Threads)
if(WIN32)
    target_sources(NesSndEmu PRIVATE SndEmu.def)
elseif(APPLE)
    set_target_properties(NesSndEmu PROPERTIES PREFIX "")
else()
    target_compile_definitions(NesSndEmu PRIVATE LINUX)
endif()

# Copy explicitly after building; the build itself never overwrites other platforms' binaries.
install(TARGETS NesSndEmu RUNTIME DESTINATION . LIBRARY DESTINATION .)
