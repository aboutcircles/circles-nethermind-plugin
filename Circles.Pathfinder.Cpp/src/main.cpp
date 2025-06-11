#include <iostream>
#include <fstream>
#include <cstdlib>
#include <libpq-fe.h>
#include "AddressIdPool.hpp"

std::string readFile(const std::string& path) {
    std::ifstream in(path);
    if (!in.is_open()) throw std::runtime_error("Unable to open " + path);
    std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    return content;
}

int main() {
    const char* connStr = std::getenv("POSTGRES_READONLY_CONNECTION_STRING");
    if (!connStr) {
        std::cerr << "POSTGRES_READONLY_CONNECTION_STRING not set" << std::endl;
        return 1;
    }

    const char* queryDir = std::getenv("PATHFINDER_QUERY_DIR");
    std::string dir = queryDir ? queryDir : std::string("Queries");
    std::string balanceQuery = readFile(dir + "/balanceQuery.sql");
    std::string trustQuery = readFile(dir + "/trustQuery.sql");

    PGconn* conn = PQconnectdb(connStr);
    if (PQstatus(conn) != CONNECTION_OK) {
        std::cerr << "Connection failed: " << PQerrorMessage(conn);
        PQfinish(conn);
        return 1;
    }

    PGresult* res = PQexec(conn, balanceQuery.c_str());
    if (PQresultStatus(res) != PGRES_TUPLES_OK) {
        std::cerr << "Balance query failed: " << PQerrorMessage(conn);
        PQclear(res);
        PQfinish(conn);
        return 1;
    }

    int balanceRows = PQntuples(res);
    for (int i = 0; i < balanceRows; ++i) {
        std::string account = PQgetvalue(res, i, 1);
        std::string token = PQgetvalue(res, i, 2);
        AddressIdPool::IdOf(account);
        AddressIdPool::IdOf(token);
    }
    PQclear(res);

    res = PQexec(conn, trustQuery.c_str());
    if (PQresultStatus(res) != PGRES_TUPLES_OK) {
        std::cerr << "Trust query failed: " << PQerrorMessage(conn);
        PQclear(res);
        PQfinish(conn);
        return 1;
    }

    int trustRows = PQntuples(res);
    for (int i = 0; i < trustRows; ++i) {
        std::string truster = PQgetvalue(res, i, 0);
        std::string trustee = PQgetvalue(res, i, 1);
        AddressIdPool::IdOf(truster);
        AddressIdPool::IdOf(trustee);
    }
    PQclear(res);
    PQfinish(conn);

    std::cout << "Balances rows: " << balanceRows << std::endl;
    std::cout << "Trust rows: " << trustRows << std::endl;

    return 0;
}

