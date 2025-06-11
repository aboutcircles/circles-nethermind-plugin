# Circles Pathfinder C++ Loader

This small command line tool loads balance and trust data from PostgreSQL
using the same queries as the .NET implementation. It prints the number of
rows returned from each query.

## Building

```bash
mkdir build && cd build
cmake ..
make
```

## Configuration

The tool relies on environment variables:

- `POSTGRES_READONLY_CONNECTION_STRING` – connection string to the database.
- `PATHFINDER_QUERY_DIR` – optional path to the directory containing
  `balanceQuery.sql` and `trustQuery.sql` (defaults to `Queries` inside the
  project directory).

## Running

```bash
POSTGRES_READONLY_CONNECTION_STRING="your conn string" ./pathfinder_cpp
```

