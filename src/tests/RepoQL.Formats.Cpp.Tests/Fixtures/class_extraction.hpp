namespace net {
class AbstractBase {
public:
    virtual void connect(const std::string& endpoint) = 0;
};

class ConnectionPool : public AbstractBase, private detail::Tracker {
public:
    ConnectionPool();
    void connect(const std::string& endpoint) override;
    constexpr int retries() const noexcept;
protected:
    virtual void shutdown() final;
private:
    static int s_instances;
    int port;
};
}
