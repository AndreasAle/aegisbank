#include "aegis/transfer.hpp"

#include <gtest/gtest.h>

#include <limits>
#include <random>

using namespace aegis;

namespace {

constexpr std::uint32_t IDR = 360;
constexpr std::uint32_t USD = 840;
constexpr std::int64_t kMax = std::numeric_limits<std::int64_t>::max();

AccountId id(std::uint8_t tag) {
    AccountId a{};
    a[15] = tag;
    return a;
}

Account deposit(std::uint8_t tag, std::int64_t balance, std::uint32_t currency = IDR) {
    return Account{id(tag), balance, currency, AccountStatus::Active, NormalSide::Credit};
}

Account vault(std::uint8_t tag, std::int64_t balance) {
    return Account{id(tag), balance, IDR, AccountStatus::Active, NormalSide::Debit};
}

TransferRequest transfer(Account from, Account to, std::int64_t amount, std::uint32_t currency = IDR) {
    return TransferRequest{from, to, amount, currency};
}

}  // namespace

TEST(Transfer, ValidTransferMovesFundsBetweenDepositAccounts) {
    const auto d = evaluate_transfer(transfer(deposit(1, 100'000), deposit(2, 5'000), 80'000));

    ASSERT_EQ(d.result, ResultCode::Ok);
    EXPECT_EQ(d.debit_account_balance_after, 20'000);
    EXPECT_EQ(d.credit_account_balance_after, 85'000);
}

TEST(Transfer, ValidTransferProducesBalancedInstructions) {
    const auto d = evaluate_transfer(transfer(deposit(1, 100'000), deposit(2, 0), 80'000));

    ASSERT_TRUE(d.approved());
    EXPECT_EQ(d.lines[0].account_id, id(1));
    EXPECT_EQ(d.lines[0].debit_minor, 80'000);
    EXPECT_EQ(d.lines[0].credit_minor, 0);
    EXPECT_EQ(d.lines[1].account_id, id(2));
    EXPECT_EQ(d.lines[1].debit_minor, 0);
    EXPECT_EQ(d.lines[1].credit_minor, 80'000);
    EXPECT_EQ(d.lines[0].debit_minor + d.lines[1].debit_minor,
              d.lines[0].credit_minor + d.lines[1].credit_minor);
}

TEST(Transfer, EntireBalanceCanBeTransferred) {
    const auto d = evaluate_transfer(transfer(deposit(1, 100'000), deposit(2, 0), 100'000));
    ASSERT_TRUE(d.approved());
    EXPECT_EQ(d.debit_account_balance_after, 0);
}

TEST(Transfer, InsufficientBalanceIsRejected) {
    const auto d = evaluate_transfer(transfer(deposit(1, 100'000), deposit(2, 0), 100'001));
    EXPECT_EQ(d.result, ResultCode::InsufficientFunds);
}

TEST(Transfer, RejectedDecisionCarriesNoInstructions) {
    const auto d = evaluate_transfer(transfer(deposit(1, 10), deposit(2, 0), 11));
    ASSERT_FALSE(d.approved());
    EXPECT_EQ(d.lines[0], LedgerInstruction{});
    EXPECT_EQ(d.lines[1], LedgerInstruction{});
    EXPECT_EQ(d.debit_account_balance_after, 0);
    EXPECT_EQ(d.credit_account_balance_after, 0);
}

TEST(Transfer, ZeroAmountIsRejected) {
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), deposit(2, 0), 0)).result,
              ResultCode::AmountNotPositive);
}

TEST(Transfer, NegativeAmountIsRejected) {
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), deposit(2, 0), -1)).result,
              ResultCode::AmountNotPositive);
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), deposit(2, 0),
                                         std::numeric_limits<std::int64_t>::min())).result,
              ResultCode::AmountNotPositive);
}

TEST(Transfer, SameAccountIsRejected) {
    EXPECT_EQ(evaluate_transfer(transfer(deposit(7, 100), deposit(7, 100), 10)).result,
              ResultCode::SameAccount);
}

TEST(Transfer, CurrencyMismatchBetweenAccountsIsRejected) {
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), deposit(2, 0, USD), 10)).result,
              ResultCode::CurrencyMismatch);
}

TEST(Transfer, CurrencyMismatchBetweenRequestAndAccountsIsRejected) {
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), deposit(2, 0), 10, USD)).result,
              ResultCode::CurrencyMismatch);
}

TEST(Transfer, ClosedSourceAccountIsRejected) {
    auto source = deposit(1, 100);
    source.status = AccountStatus::Closed;
    EXPECT_EQ(evaluate_transfer(transfer(source, deposit(2, 0), 10)).result,
              ResultCode::DebitAccountState);
}

TEST(Transfer, ClosedDestinationAccountIsRejected) {
    auto destination = deposit(2, 0);
    destination.status = AccountStatus::Closed;
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), destination, 10)).result,
              ResultCode::CreditAccountState);
}

TEST(Transfer, FrozenAccountsAreRejected) {
    auto frozen = deposit(1, 100);
    frozen.status = AccountStatus::Frozen;
    EXPECT_EQ(evaluate_transfer(transfer(frozen, deposit(2, 0), 10)).result,
              ResultCode::DebitAccountState);

    auto frozen_destination = deposit(2, 0);
    frozen_destination.status = AccountStatus::Frozen;
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), frozen_destination, 10)).result,
              ResultCode::CreditAccountState);
}

TEST(Transfer, DestinationOverflowIsRejected) {
    const auto d = evaluate_transfer(transfer(deposit(1, 100), deposit(2, kMax - 5), 6));
    EXPECT_EQ(d.result, ResultCode::BalanceOverflow);
}

TEST(Transfer, DestinationAtExactLimitIsAccepted) {
    const auto d = evaluate_transfer(transfer(deposit(1, 100), deposit(2, kMax - 5), 5));
    ASSERT_TRUE(d.approved());
    EXPECT_EQ(d.credit_account_balance_after, kMax);
}

TEST(Transfer, MaximumAmountAgainstSmallBalanceIsInsufficientNotUndefined) {
    const auto d = evaluate_transfer(transfer(deposit(1, 100), deposit(2, 0), kMax));
    EXPECT_EQ(d.result, ResultCode::InsufficientFunds);
}

TEST(Transfer, NegativeInputBalanceIsInvalidData) {
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, -1), deposit(2, 0), 10)).result,
              ResultCode::InvalidAccountData);
}

TEST(Transfer, UnknownStatusOrSideIsInvalidData) {
    auto bad_status = deposit(1, 100);
    bad_status.status = static_cast<AccountStatus>(9);
    EXPECT_EQ(evaluate_transfer(transfer(bad_status, deposit(2, 0), 10)).result,
              ResultCode::InvalidAccountData);

    auto bad_side = deposit(2, 0);
    bad_side.normal_side = static_cast<NormalSide>(0);
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 100), bad_side, 10)).result,
              ResultCode::InvalidAccountData);
}

// Opening funds: debit the asset (vault cash grows), credit the deposit.
TEST(Transfer, OpeningDepositIncreasesBothAssetAndLiability) {
    const auto d = evaluate_transfer(transfer(vault(9, 0), deposit(1, 0), 250'000));
    ASSERT_TRUE(d.approved());
    EXPECT_EQ(d.debit_account_balance_after, 250'000);
    EXPECT_EQ(d.credit_account_balance_after, 250'000);
}

// Reversal of an opening deposit: debit the deposit, credit the asset.
TEST(Transfer, CreditingAnAssetAccountRequiresSufficientAssetBalance) {
    EXPECT_EQ(evaluate_transfer(transfer(deposit(1, 500), vault(9, 100), 200)).result,
              ResultCode::InsufficientFunds);
    const auto ok = evaluate_transfer(transfer(deposit(1, 500), vault(9, 300), 200));
    ASSERT_TRUE(ok.approved());
    EXPECT_EQ(ok.debit_account_balance_after, 300);
    EXPECT_EQ(ok.credit_account_balance_after, 100);
}

TEST(Transfer, AssetOverflowOnDebitIsRejected) {
    EXPECT_EQ(evaluate_transfer(transfer(vault(9, kMax), deposit(1, 0), 1)).result,
              ResultCode::BalanceOverflow);
}

TEST(Transfer, ValidationOrderIsStable) {
    // Several rules are violated at once; the documented first rule wins.
    auto closed = deposit(3, 0, USD);
    closed.status = AccountStatus::Closed;
    EXPECT_EQ(evaluate_transfer(transfer(closed, closed, 0)).result, ResultCode::AmountNotPositive);
    EXPECT_EQ(evaluate_transfer(transfer(closed, closed, 5)).result, ResultCode::SameAccount);
    EXPECT_EQ(evaluate_transfer(transfer(closed, deposit(4, 0), 5)).result, ResultCode::CurrencyMismatch);
}

TEST(Transfer, RepeatedEvaluationIsDeterministic) {
    const auto request = transfer(deposit(1, 1'000'000), deposit(2, 42), 123'456);
    const auto first = evaluate_transfer(request);
    for (int i = 0; i < 10'000; ++i) {
        ASSERT_EQ(evaluate_transfer(request), first);
    }
}

TEST(Transfer, RandomisedTransfersConserveFundsAndNeverGoNegative) {
    std::mt19937_64 rng(20260101);
    std::uniform_int_distribution<std::int64_t> balance(0, 10'000'000);
    std::uniform_int_distribution<std::int64_t> amount(-10, 12'000'000);

    for (int i = 0; i < 200'000; ++i) {
        const auto source = deposit(1, balance(rng));
        const auto destination = deposit(2, balance(rng));
        const auto value = amount(rng);
        const auto d = evaluate_transfer(transfer(source, destination, value));

        if (d.approved()) {
            ASSERT_GT(value, 0);
            ASSERT_GE(d.debit_account_balance_after, 0);
            ASSERT_EQ(d.debit_account_balance_after + d.credit_account_balance_after,
                      source.balance_minor + destination.balance_minor);
            ASSERT_EQ(d.lines[0].debit_minor, d.lines[1].credit_minor);
        } else {
            ASSERT_TRUE(value <= 0 || value > source.balance_minor);
        }
    }
}

TEST(Transfer, ResultNamesAreStable) {
    EXPECT_EQ(result_name(ResultCode::Ok), "OK");
    EXPECT_EQ(result_name(ResultCode::InsufficientFunds), "INSUFFICIENT_FUNDS");
    EXPECT_EQ(result_name(static_cast<ResultCode>(12345)), "UNKNOWN");
}
