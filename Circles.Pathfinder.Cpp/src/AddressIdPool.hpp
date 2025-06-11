#ifndef ADDRESS_ID_POOL_HPP
#define ADDRESS_ID_POOL_HPP

#include <string>
#include <unordered_map>
#include <vector>
#include <mutex>

class AddressIdPool {
public:
    static int IdOf(const std::string& address);
    static int BalanceNodeIdOf(const std::string& address);
    static const std::string& StringOf(int id);
    static bool IsBalanceNode(int id);
    static std::vector<std::string> GetAvatarSnapshot();

private:
    static std::unordered_map<std::string, int> map;
    static std::unordered_map<std::string, int> balanceNodeMap;
    static std::vector<std::string> reverse;
    static int nextId;
    static std::mutex mutex;
};

#endif
