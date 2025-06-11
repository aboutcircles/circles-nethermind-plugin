#include "AddressIdPool.hpp"
#include <algorithm>

std::unordered_map<std::string, int> AddressIdPool::map;
std::unordered_map<std::string, int> AddressIdPool::balanceNodeMap;
std::vector<std::string> AddressIdPool::reverse;
int AddressIdPool::nextId = 0;
std::mutex AddressIdPool::mutex;

int AddressIdPool::IdOf(const std::string& address) {
    std::string lower = address;
    std::transform(lower.begin(), lower.end(), lower.begin(), ::tolower);
    std::lock_guard<std::mutex> lock(mutex);
    auto it = map.find(lower);
    if (it != map.end()) return it->second;
    int id = nextId++;
    map[lower] = id;
    reverse.push_back(lower);
    return id;
}

int AddressIdPool::BalanceNodeIdOf(const std::string& address) {
    std::string lower = address;
    std::transform(lower.begin(), lower.end(), lower.begin(), ::tolower);
    std::lock_guard<std::mutex> lock(mutex);
    auto it = map.find(lower);
    if (it != map.end()) return it->second;
    int id = nextId++;
    map[lower] = id;
    balanceNodeMap[lower] = id;
    reverse.push_back(lower);
    return id;
}

const std::string& AddressIdPool::StringOf(int id) {
    return reverse[id];
}

bool AddressIdPool::IsBalanceNode(int id) {
    std::lock_guard<std::mutex> lock(mutex);
    return balanceNodeMap.count(reverse[id]) > 0;
}

std::vector<std::string> AddressIdPool::GetAvatarSnapshot() {
    std::lock_guard<std::mutex> lock(mutex);
    std::vector<std::string> result;
    for (const auto& s : reverse) {
        if (s.find('-') == std::string::npos) result.push_back(s);
    }
    return result;
}

