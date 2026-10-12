# The project check-mutations sweeps for its self-test and its end-to-end tests: a library, and a test binary that
# writes its own JUnit report, so nothing is fetched to build it. Embedded into DssHarness, so every copy of the tool
# carries the very fixture its tests swept; arms.txt declares an arm to each verdict an arm's design can reach on any
# machine, one whose mutation is coupled across two files, and one whose mutation is two sites of one file.
#
# C is enabled beside C++ though no source is C: a toolchain may hold both compilers to an id, and a language the
# project never enables is one CMake identifies no compiler for.
cmake_minimum_required(VERSION 3.20)
project(mutation_fixture LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(fixture STATIC src/fixture.cpp)
target_include_directories(fixture PUBLIC src)

add_executable(fixture_tests tests/fixture_tests.cpp)
target_link_libraries(fixture_tests PRIVATE fixture)
