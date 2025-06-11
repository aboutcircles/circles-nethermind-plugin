cmake_minimum_required(VERSION 3.10)
project(pathfinder_cpp)

set(CMAKE_CXX_STANDARD 17)

find_package(PostgreSQL REQUIRED)
include_directories(${PostgreSQL_INCLUDE_DIRS})

add_executable(pathfinder_cpp
    src/main.cpp
    src/AddressIdPool.cpp)

target_link_libraries(pathfinder_cpp ${PostgreSQL_LIBRARIES})

install(TARGETS pathfinder_cpp RUNTIME DESTINATION bin)
