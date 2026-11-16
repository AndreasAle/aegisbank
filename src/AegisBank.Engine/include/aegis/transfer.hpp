// AegisBank transaction engine - internal C++20 domain API.
// This header is for C++ consumers (tests, benchmarks). Other languages use
// the C ABI in engine.h.
#pragma once

#include <array>
#include <compare>
#include <concepts>
#include <cstdint>
#include <limits>
#include <optional>
#include <string_view>

namespace aegis {

enum class ResultCode : std::int32_t {
    Ok                 = 0,
    NullArgument       = 1,
    AbiMismatch        = 2,
    AmountNotPositive  = 10,
    SameAccount        = 11,
    CurrencyMismatch   = 12,
    DebitAccountState  = 13,
    CreditAccountState = 14,
    InsufficientFunds  = 15,
    BalanceOverflow    = 16,
    InvalidAccountData = 17,
    Internal           = 99,
};

enum class AccountStatus : std::uint8_t { Active = 1, Frozen = 2, Closed = 3 };
enum class NormalSide : std::uint8_t { Debit = 1, Credit = 2 };

using AccountId = std::array<std::uint8_t, 16>;

struct Account {
    AccountId id{};
    std::int64_t balance_minor{};
    std::uint32_t currency{};
    AccountStatus status{AccountStatus::Active};
    NormalSide normal_side{NormalSide::Credit};
};

struct TransferRequest {
    Account debit_account;
    Account credit_account;
    std::int64_t amount_minor{};
    std::uint32_t currency{};
};

struct LedgerInstruction {
    AccountId account_id{};
    std::int64_t debit_minor{};
    std::int64_t credit_minor{};

    friend constexpr bool operator==(const LedgerInstruction&, const LedgerInstruction&) = default;
};

struct TransferDecision {
    ResultCode result{ResultCode::Internal};
    std::int64_t debit_account_balance_after{};
    std::int64_t credit_account_balance_after{};
    std::array<LedgerInstruction, 2> lines{};

    [[nodiscard]] constexpr bool approved() const noexcept { return result == ResultCode::Ok; }
    friend constexpr bool operator==(const TransferDecision&, const TransferDecision&) = default;
};

// Checked arithmetic. Returns nullopt instead of wrapping.
template <std::signed_integral T>
[[nodiscard]] constexpr std::optional<T> checked_add(T a, T b) noexcept {
    if (b > 0 && a > std::numeric_limits<T>::max() - b) return std::nullopt;
    if (b < 0 && a < std::numeric_limits<T>::min() - b) return std::nullopt;
    return static_cast<T>(a + b);
}

template <std::signed_integral T>
[[nodiscard]] constexpr std::optional<T> checked_sub(T a, T b) noexcept {
    if (b < 0 && a > std::numeric_limits<T>::max() + b) return std::nullopt;
    if (b > 0 && a < std::numeric_limits<T>::min() + b) return std::nullopt;
    return static_cast<T>(a - b);
}

// Pure function: same input always yields the same decision. No I/O, no
// global state, no allocation.
[[nodiscard]] TransferDecision evaluate_transfer(const TransferRequest& request) noexcept;

[[nodiscard]] std::string_view result_name(ResultCode code) noexcept;

}  // namespace aegis
