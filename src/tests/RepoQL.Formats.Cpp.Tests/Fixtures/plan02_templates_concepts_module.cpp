template <typename T, int N>
struct Buffer {
    T data[N];
};

template <>
struct Buffer<int, 3> {
    int data[3];
};

template <typename T>
concept Addable = requires(T a, T b) {
    a + b;
};

export module net:impl;
