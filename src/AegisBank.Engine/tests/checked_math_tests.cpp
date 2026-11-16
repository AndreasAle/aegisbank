#include "aegis/transfer.hpp"

#include <gtest/gtest.h>

#include <cstdint>
#include <limits>

using aegis::checked_add;
using aegis::checked_sub;

namespace {
constexpr std::int64_t kMax = std::numeric_limits<std::int64_t>::max();
constexpr std::int64_t kMin = std::numeric_limits<std::int64_t>::min();
}  // namespace

TEST(CheckedMath, AddWithinRange) {
    EXPECT_EQ(checked_add<std::int64_t>(2, 3), 5);
    EXPECT_EQ(checked_add<std::int64_t>(kMax - 1, 1), kMax);
    EXPECT_EQ(checked_add<std::int64_t>(kMin + 1, -1), kMin);
    EXPECT_EQ(checked_add<std::int64_t>(kMax, kMin), -1);
}

TEST(CheckedMath, AddOverflowAndUnderflow) {
    EXPECT_FALSE(checked_add<std::int64_t>(kMax, 1).has_value());
    EXPECT_FALSE(checked_add<std::int64_t>(kMax, kMax).has_value());
    EXPECT_FALSE(checked_add<std::int64_t>(kMin, -1).has_value());
}

TEST(CheckedMath, SubWithinRange) {
    EXPECT_EQ(checked_sub<std::int64_t>(5, 3), 2);
    EXPECT_EQ(checked_sub<std::int64_t>(kMin + 1, 1), kMin);
    EXPECT_EQ(checked_sub<std::int64_t>(kMax - 1, -1), kMax);
    EXPECT_EQ(checked_sub<std::int64_t>(0, kMax), kMin + 1);
}

TEST(CheckedMath, SubOverflowAndUnderflow) {
    EXPECT_FALSE(checked_sub<std::int64_t>(kMin, 1).has_value());
    EXPECT_FALSE(checked_sub<std::int64_t>(kMax, -1).has_value());
    EXPECT_FALSE(checked_sub<std::int64_t>(0, kMin).has_value());
}

TEST(CheckedMath, IsUsableInConstantExpressions) {
    static_assert(checked_add<std::int32_t>(1, 2) == 3);
    static_assert(!checked_add<std::int32_t>(std::numeric_limits<std::int32_t>::max(), 1));
    static_assert(!checked_sub<std::int8_t>(-128, 1));
    SUCCEED();
}
