cmake_minimum_required(VERSION 3.16)
project(openpst VERSION 0.3.0 LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

option(OPST_BUILD_SHARED "Build the shared library (openpst.dll / libopenpst.so)" ON)
option(OPST_BUILD_TOOLS  "Build the command line tool"                           ON)
option(OPST_BUILD_TESTS  "Build the tests"                                       ON)
option(OPST_SANITIZE     "Build with AddressSanitizer + UBSan (GCC/Clang)"       OFF)

set(OPST_SOURCES
    src/op_platform.c
    src/op_util.c
    src/op_ndb.c
    src/op_ltp.c
    src/op_rtf.c
    src/op_search.c
    src/op_wr.c
    src/op_edit.c
    src/op_edit2.c
    src/op_ops.c
    src/op_folders.c
    src/op_api_w.c
    src/op_npm.c
    src/op_xcopy.c
    src/op_fix.c
    src/op_check.c
    src/op_test.c
    src/op_msg.c)

if(MSVC)
    add_compile_options(/W4 /utf-8 /D_CRT_SECURE_NO_WARNINGS)
else()
    add_compile_options(-Wall -Wextra -Wno-unused-parameter)
    if(OPST_SANITIZE)
        add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer -g)
        add_link_options(-fsanitize=address,undefined)
    endif()
endif()

# static library (also the object set used for tests and the tool)
add_library(openpst_static STATIC ${OPST_SOURCES})
target_include_directories(openpst_static PUBLIC include PRIVATE src)
if(WIN32)
    # the DLL's import library is openpst.lib; keep the static archive distinct
    set_target_properties(openpst_static PROPERTIES OUTPUT_NAME openpst_static)
else()
    set_target_properties(openpst_static PROPERTIES OUTPUT_NAME openpst)
endif()

if(OPST_BUILD_SHARED)
    add_library(openpst SHARED ${OPST_SOURCES})
    target_include_directories(openpst PUBLIC include PRIVATE src)
    target_compile_definitions(openpst PRIVATE OPST_BUILD PUBLIC OPST_SHARED)
    set_target_properties(openpst PROPERTIES C_VISIBILITY_PRESET hidden VERSION ${PROJECT_VERSION} SOVERSION 0)
endif()

if(OPST_BUILD_TOOLS)
    add_executable(openpst_cli tools/openpst_cli.c)
    target_link_libraries(openpst_cli PRIVATE openpst_static)
    if(WIN32)
        target_link_libraries(openpst_cli PRIVATE shell32)
    endif()
    set_target_properties(openpst_cli PROPERTIES OUTPUT_NAME openpst)
endif()

if(OPST_BUILD_TESTS AND EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/tests/test_basic.c)
    enable_testing()
    add_executable(test_basic tests/test_basic.c)
    target_link_libraries(test_basic PRIVATE openpst_static)
    add_test(NAME basic COMMAND test_basic)
endif()

if(OPST_BUILD_TESTS AND EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/tests/test_write.c)
    add_executable(test_write tests/test_write.c)
    target_link_libraries(test_write PRIVATE openpst_static)
    add_test(NAME write COMMAND test_write)
endif()

# robustness test for the RTF / HTML converters (run by hand against a real file, preferably with OPST_SANITIZE=ON)
if(OPST_BUILD_TESTS AND EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/tests/fuzz_rtf.c)
    add_executable(fuzz_rtf tests/fuzz_rtf.c)
    target_link_libraries(fuzz_rtf PRIVATE openpst_static)
endif()

install(TARGETS openpst_static ARCHIVE DESTINATION lib)
if(OPST_BUILD_SHARED)
    install(TARGETS openpst LIBRARY DESTINATION lib RUNTIME DESTINATION bin ARCHIVE DESTINATION lib)
endif()
if(OPST_BUILD_TOOLS)
    install(TARGETS openpst_cli RUNTIME DESTINATION bin)
endif()
install(FILES include/openpst.h DESTINATION include)
