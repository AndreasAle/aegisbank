#include "aegis/transfer.hpp"

namespace aegis {
namespace {

constexpr bool valid_status(AccountStatus s) noexcept {
    return s == AccountStatus::Active || s == AccountStatus::Frozen || s == AccountStatus::Closed;
}

constexpr bool valid_side(NormalSide s) noexcept {
    return s == NormalSide::Debit || s == NormalSide::Credit;
}

constexpr bool valid_account(const Account& a) noexcept {
    return valid_status(a.status) && valid_side(a.normal_side) && a.balance_minor >= 0 && a.currency != 0;
}

struct Applied {
    ResultCode result;
    std::int64_t balance;
};

// Applies a posting on `posting_side` to an account. A posting on the
// account's normal side increases its balance; a posting on the opposite
// side decreases it and must not take it below zero.
constexpr Applied apply(const Account& account, NormalSide posting_side, std::int64_t amount) noexcept {
    if (account.normal_side == posting_side) {
        const auto sum = checked_add(account.balance_minor, amount);
        if (!sum) return {ResultCode::BalanceOverflow, 0};
        return {ResultCode::Ok, *sum};
    }
    const auto diff = checked_sub(account.balance_minor, amount);
    if (!diff) return {ResultCode::BalanceOverflow, 0};
    if (*diff < 0) return {ResultCode::InsufficientFunds, 0};
    return {ResultCode::Ok, *diff};
}

constexpr TransferDecision reject(ResultCode code) noexcept {
    TransferDecision d{};
    d.result = code;
    return d;
}

}  // namespace

TransferDecision evaluate_transfer(const TransferRequest& request) noexcept {
    const Account& debit = request.debit_account;
    const Account& credit = request.credit_account;

    // The order of these checks is part of the engine's contract: the first
    // failing rule determines the result code.
    if (request.amount_minor <= 0) return reject(ResultCode::AmountNotPositive);
    if (!valid_account(debit) || !valid_account(credit)) return reject(ResultCode::InvalidAccountData);
    if (debit.id == credit.id) return reject(ResultCode::SameAccount);
    if (debit.currency != request.currency || credit.currency != request.currency) {
        return reject(ResultCode::CurrencyMismatch);
    }
    if (debit.status != AccountStatus::Active) return reject(ResultCode::DebitAccountState);
    if (credit.status != AccountStatus::Active) return reject(ResultCode::CreditAccountState);

    const Applied debit_after = apply(debit, NormalSide::Debit, request.amount_minor);
    if (debit_after.result != ResultCode::Ok) return reject(debit_after.result);

    const Applied credit_after = apply(credit, NormalSide::Credit, request.amount_minor);
    if (credit_after.result != ResultCode::Ok) return reject(credit_after.result);

    TransferDecision decision{};
    decision.result = ResultCode::Ok;
    decision.debit_account_balance_after = debit_after.balance;
    decision.credit_account_balance_after = credit_after.balance;
    decision.lines[0].account_id = debit.id;
    decision.lines[0].debit_minor = request.amount_minor;
    decision.lines[1].account_id = credit.id;
    decision.lines[1].credit_minor = request.amount_minor;
    return decision;
}

std::string_view result_name(ResultCode code) noexcept {
    switch (code) {
        case ResultCode::Ok:                 return "OK";
        case ResultCode::NullArgument:       return "NULL_ARGUMENT";
        case ResultCode::AbiMismatch:        return "ABI_MISMATCH";
        case ResultCode::AmountNotPositive:  return "AMOUNT_NOT_POSITIVE";
        case ResultCode::SameAccount:        return "SAME_ACCOUNT";
        case ResultCode::CurrencyMismatch:   return "CURRENCY_MISMATCH";
        case ResultCode::DebitAccountState:  return "DEBIT_ACCOUNT_NOT_ACTIVE";
        case ResultCode::CreditAccountState: return "CREDIT_ACCOUNT_NOT_ACTIVE";
        case ResultCode::InsufficientFunds:  return "INSUFFICIENT_FUNDS";
        case ResultCode::BalanceOverflow:    return "BALANCE_OVERFLOW";
        case ResultCode::InvalidAccountData: return "INVALID_ACCOUNT_DATA";
        case ResultCode::Internal:           return "INTERNAL_ERROR";
    }
    return "UNKNOWN";
}

}  // namespace aegis
