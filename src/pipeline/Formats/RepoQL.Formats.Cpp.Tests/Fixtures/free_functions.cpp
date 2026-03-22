namespace net {
inline int connect(int retries, const std::string& endpoint) noexcept {
    return retries + static_cast<int>(endpoint.size());
}

constexpr int compute(int lhs, int rhs) {
    return lhs + rhs;
}

static int local_only(int value) {
    return value;
}
}
