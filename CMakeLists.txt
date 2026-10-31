cmake_minimum_required(VERSION 3.24)

project(HDRControlGui VERSION 1.0.0 LANGUAGES CXX)

add_executable(HDRControl WIN32
    src/main.cpp
)

target_compile_features(HDRControl PRIVATE cxx_std_23)
target_compile_definitions(HDRControl PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    WINVER=0x0A00
    _WIN32_WINNT=0x0A00
)
target_link_libraries(HDRControl PRIVATE comctl32 user32)

if(MSVC)
    target_compile_options(HDRControl PRIVATE /W4 /permissive- /utf-8)
endif()
