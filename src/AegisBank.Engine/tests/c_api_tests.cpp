#include "aegis/engine.h"

#include <gtest/gtest.h>

#include <cstring>
#include <limits>
#include <string>
#include <vector>

namespace {

aegis_account_snapshot account(uint8_t tag, int64_t balance, uint8_t side = AEGIS_NORMAL_CREDIT) {
    aegis_account_snapshot a{};
    a.account_id[0] = tag;
    a.balance_minor = balance;
    a.currency = 360;
    a.status = AEGIS_ACCOUNT_ACTIVE;
    a.normal_side = side;
    return a;
}

aegis_transfer_request request(int64_t source_balance, int64_t destination_balance, int64_t amount) {
    aegis_transfer_request r{};
    r.struct_size = sizeof(r);
    r.abi_version = AEGIS_ABI_VERSION;
    r.debit_account = account(1, source_balance);
    r.credit_account = account(2, destination_balance);
    r.amount_minor = amount;
    r.currency = 360;
    return r;
}

aegis_transfer_decision empty_decision() {
    aegis_transfer_decision d{};
    d.struct_size = sizeof(d);
    return d;
}

}  // namespace

TEST(CApi, ReportsAbiVersionAndLayout) {
    EXPECT_EQ(aegis_abi_version(), AEGIS_ABI_VERSION);
    EXPECT_EQ(aegis_sizeof_account_snapshot(), 32u);
    EXPECT_EQ(aegis_sizeof_transfer_request(), 88u);
    EXPECT_EQ(aegis_sizeof_ledger_instruction(), 32u);
    EXPECT_EQ(aegis_sizeof_transfer_decision(), 96u);
    EXPECT_EQ(aegis_offsetof_request_amount(), 72u);
    EXPECT_EQ(aegis_offsetof_decision_lines(), 32u);
    EXPECT_FALSE(std::string(aegis_engine_version()).empty());
}

TEST(CApi, ApprovesValidTransfer) {
    const auto r = request(100'000, 0, 80'000);
    auto d = empty_decision();

    ASSERT_EQ(aegis_evaluate_transfer(&r, &d), AEGIS_OK);
    EXPECT_EQ(d.result_code, AEGIS_OK);
    EXPECT_EQ(d.abi_version, AEGIS_ABI_VERSION);
    EXPECT_EQ(d.debit_account_balance_after, 20'000);
    EXPECT_EQ(d.credit_account_balance_after, 80'000);
    EXPECT_EQ(std::memcmp(d.lines[0].account_id, r.debit_account.account_id, 16), 0);
    EXPECT_EQ(std::memcmp(d.lines[1].account_id, r.credit_account.account_id, 16), 0);
    EXPECT_EQ(d.lines[0].debit_minor, 80'000);
    EXPECT_EQ(d.lines[1].credit_minor, 80'000);
}

TEST(CApi, MapsDomainErrorsToResultCodes) {
    auto d = empty_decision();

    auto insufficient = request(100, 0, 101);
    EXPECT_EQ(aegis_evaluate_transfer(&insufficient, &d), AEGIS_ERR_INSUFFICIENT_FUNDS);

    auto zero = request(100, 0, 0);
    EXPECT_EQ(aegis_evaluate_transfer(&zero, &d), AEGIS_ERR_AMOUNT_NOT_POSITIVE);

    auto overflow = request(100, std::numeric_limits<int64_t>::max(), 1);
    EXPECT_EQ(aegis_evaluate_transfer(&overflow, &d), AEGIS_ERR_BALANCE_OVERFLOW);

    auto same = request(100, 0, 10);
    same.credit_account = same.debit_account;
    EXPECT_EQ(aegis_evaluate_transfer(&same, &d), AEGIS_ERR_SAME_ACCOUNT);

    auto currency = request(100, 0, 10);
    currency.credit_account.currency = 840;
    EXPECT_EQ(aegis_evaluate_transfer(&currency, &d), AEGIS_ERR_CURRENCY_MISMATCH);

    auto closed = request(100, 0, 10);
    closed.debit_account.status = AEGIS_ACCOUNT_CLOSED;
    EXPECT_EQ(aegis_evaluate_transfer(&closed, &d), AEGIS_ERR_DEBIT_ACCOUNT_STATE);
}

TEST(CApi, RejectsNullArguments) {
    const auto r = request(100, 0, 10);
    auto d = empty_decision();

    EXPECT_EQ(aegis_evaluate_transfer(&r, nullptr), AEGIS_ERR_NULL_ARGUMENT);
    EXPECT_EQ(aegis_evaluate_transfer(nullptr, &d), AEGIS_ERR_NULL_ARGUMENT);
    EXPECT_EQ(d.result_code, AEGIS_ERR_NULL_ARGUMENT);
}

TEST(CApi, RejectsMismatchedStructSizeOrVersion) {
    auto d = empty_decision();

    auto wrong_size = request(100, 0, 10);
    wrong_size.struct_size = sizeof(wrong_size) - 8;
    EXPECT_EQ(aegis_evaluate_transfer(&wrong_size, &d), AEGIS_ERR_ABI_MISMATCH);

    auto wrong_version = request(100, 0, 10);
    wrong_version.abi_version = AEGIS_ABI_VERSION + 1;
    EXPECT_EQ(aegis_evaluate_transfer(&wrong_version, &d), AEGIS_ERR_ABI_MISMATCH);
}

TEST(CApi, DoesNotWriteToDecisionOfUnexpectedSize) {
    const auto r = request(100, 0, 10);
    aegis_transfer_decision d;
    std::memset(&d, 0xAB, sizeof(d));
    d.struct_size = 16;  // caller compiled against a smaller struct

    EXPECT_EQ(aegis_evaluate_transfer(&r, &d), AEGIS_ERR_ABI_MISMATCH);
    const auto* bytes = reinterpret_cast<const unsigned char*>(&d);
    for (size_t i = sizeof(uint32_t); i < sizeof(d); ++i) {
        ASSERT_EQ(bytes[i], 0xAB) << "byte " << i << " was modified";
    }
}

TEST(CApi, ClearsStaleOutputOnRejection) {
    const auto ok = request(100, 0, 10);
    auto d = empty_decision();
    ASSERT_EQ(aegis_evaluate_transfer(&ok, &d), AEGIS_OK);

    const auto bad = request(100, 0, 1'000);
    ASSERT_EQ(aegis_evaluate_transfer(&bad, &d), AEGIS_ERR_INSUFFICIENT_FUNDS);
    EXPECT_EQ(d.debit_account_balance_after, 0);
    EXPECT_EQ(d.lines[0].debit_minor, 0);
    EXPECT_EQ(d.lines[1].credit_minor, 0);
}

TEST(CApi, BatchEvaluatesEachRequestIndependently) {
    std::vector<aegis_transfer_request> requests{
        request(100, 0, 60), request(100, 0, 600), request(100, 0, 100)};
    std::vector<aegis_transfer_decision> decisions(requests.size(), empty_decision());

    ASSERT_EQ(aegis_evaluate_transfer_batch(requests.data(), decisions.data(), requests.size()), AEGIS_OK);
    EXPECT_EQ(decisions[0].result_code, AEGIS_OK);
    EXPECT_EQ(decisions[1].result_code, AEGIS_ERR_INSUFFICIENT_FUNDS);
    EXPECT_EQ(decisions[2].result_code, AEGIS_OK);
    EXPECT_EQ(decisions[2].debit_account_balance_after, 0);
}

TEST(CApi, BatchArgumentValidation) {
    EXPECT_EQ(aegis_evaluate_transfer_batch(nullptr, nullptr, 0), AEGIS_OK);
    EXPECT_EQ(aegis_evaluate_transfer_batch(nullptr, nullptr, 3), AEGIS_ERR_NULL_ARGUMENT);
}

TEST(CApi, ResultNamesAreNeverNull) {
    for (int32_t code = -5; code < 120; ++code) {
        ASSERT_NE(aegis_result_name(code), nullptr);
    }
    EXPECT_STREQ(aegis_result_name(AEGIS_ERR_INSUFFICIENT_FUNDS), "INSUFFICIENT_FUNDS");
}
