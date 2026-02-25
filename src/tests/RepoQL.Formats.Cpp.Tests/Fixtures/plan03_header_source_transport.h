namespace net {
class ConnectionPool {
public:
    void connect(int retries);
    void connect(long retries);
    void shutdown();
};
}
