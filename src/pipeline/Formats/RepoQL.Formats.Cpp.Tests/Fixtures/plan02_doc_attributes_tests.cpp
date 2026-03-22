/**
 * @brief Computes a value.
 * @param x input value
 * @returns output value
 * @see old_api
 */
[[nodiscard]]
int compute(int x)
{
    return x + 1;
}

/// @deprecated use newer_api
[[deprecated("use newer_api")]]
int old_api()
{
    return 0;
}

TEST_F(SuiteName, HandlesCase)
{
    EXPECT_EQ(1, 1);
}
