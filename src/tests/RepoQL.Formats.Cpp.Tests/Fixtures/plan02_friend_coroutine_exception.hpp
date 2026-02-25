class FriendTarget;

class Host {
public:
    friend class FriendTarget;
    unsigned int flags : 4;
    int (*handler)(int);
    int log(const char* fmt, ...);
};

class FriendTarget {};

int do_work()
{
    try {
        throw 42;
    } catch (const std::exception& ex) {
        throw;
    }

    return 0;
}

auto stream_values()
{
    co_return;
}
