namespace net::internal {
struct Endpoint {
    int port;
    void reset();
};

enum class State : uint8_t {
    Disconnected = 0,
    Connecting = 1
};

enum ErrorCode {
    None = 0,
    Timeout = 100
};
}
