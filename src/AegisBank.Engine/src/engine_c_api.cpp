#include "aegis/engine.h"
#include "aegis/transfer.hpp"

#include <cstddef>
#include <cstring>
#include <type_traits>

// The ABI layout is fixed. If any of these fail the header was changed
// without bumping AEGIS_ABI_VERSION.
static_assert(sizeof(aegis_account_snapshot) == 32);
static_assert(sizeof(aegis_transfer_request) == 88);
static_assert(sizeof(aegis_ledger_instruction) == 32);
static_assert(sizeof(aegis_transfer_decision) == 96);
static_assert(offsetof(aegis_transfer_request, amount_minor) == 72);
static_assert(offsetof(aegis_transfer_decision, lines) == 32);
static_assert(std::is_standard_layout_v<aegis_transfer_request>);
static_assert(std::is_trivially_copyable_v<aegis_transfer_decision>);

namespace {

// Fills the domain account in place (no temporary struct, see transfer.cpp).
void fill_account(aegis::Account& a, const aegis_account_snapshot& s) noexcept {
    std::memcpy(a.id.data(), s.account_id, sizeof(s.account_id));
    a.balance_minor = s.balance_minor;
    a.currency = s.currency;
    a.status = static_cast<aegis::AccountStatus>(s.status);
    a.normal_side = static_cast<aegis::NormalSide>(s.normal_side);
}

void write_line(aegis_ledger_instruction& out, const aegis::LedgerInstruction& in) noexcept {
    std::memcpy(out.account_id, in.account_id.data(), sizeof(out.account_id));
    out.debit_minor = in.debit_minor;
    out.credit_minor = in.credit_minor;
}

void clear(aegis_ledger_instruction& line) noexcept {
    std::memset(line.account_id, 0, sizeof(line.account_id));
    line.debit_minor = 0;
    line.credit_minor = 0;
}

// Puts the decision into its "no result" state, field by field, so that every
// byte the caller can read is defined before any result is written.
void reset(aegis_transfer_decision& d) noexcept {
    d.struct_size = sizeof(aegis_transfer_decision);
    d.abi_version = AEGIS_ABI_VERSION;
    d.result_code = AEGIS_ERR_INTERNAL;
    d.reserved = 0;
    d.debit_account_balance_after = 0;
    d.credit_account_balance_after = 0;
    clear(d.lines[0]);
    clear(d.lines[1]);
}

int32_t evaluate_one(const aegis_transfer_request* request, aegis_transfer_decision* decision) noexcept {
    if (decision == nullptr) return AEGIS_ERR_NULL_ARGUMENT;
    if (decision->struct_size != sizeof(aegis_transfer_decision)) return AEGIS_ERR_ABI_MISMATCH;

    reset(*decision);

    if (request == nullptr) {
        decision->result_code = AEGIS_ERR_NULL_ARGUMENT;
        return decision->result_code;
    }
    if (request->struct_size != sizeof(aegis_transfer_request) ||
        request->abi_version != AEGIS_ABI_VERSION) {
        decision->result_code = AEGIS_ERR_ABI_MISMATCH;
        return decision->result_code;
    }

    try {
        aegis::TransferRequest domain;
        fill_account(domain.debit_account, request->debit_account);
        fill_account(domain.credit_account, request->credit_account);
        domain.amount_minor = request->amount_minor;
        domain.currency = request->currency;

        const aegis::TransferDecision result = aegis::evaluate_transfer(domain);
        decision->result_code = static_cast<int32_t>(result.result);
        if (result.approved()) {
            decision->debit_account_balance_after = result.debit_account_balance_after;
            decision->credit_account_balance_after = result.credit_account_balance_after;
            write_line(decision->lines[0], result.lines[0]);
            write_line(decision->lines[1], result.lines[1]);
        }
    } catch (...) {
        // The domain code is noexcept today; this guard keeps the guarantee
        // that nothing ever unwinds into the caller's runtime.
        reset(*decision);
    }
    return decision->result_code;
}

}  // namespace

extern "C" {

int32_t AEGIS_CALL aegis_evaluate_transfer(const aegis_transfer_request* request,
                                           aegis_transfer_decision* decision) {
    return evaluate_one(request, decision);
}

int32_t AEGIS_CALL aegis_evaluate_transfer_batch(const aegis_transfer_request* requests,
                                                 aegis_transfer_decision* decisions,
                                                 size_t count) {
    if (count == 0) return AEGIS_OK;
    if (requests == nullptr || decisions == nullptr) return AEGIS_ERR_NULL_ARGUMENT;
    for (size_t i = 0; i < count; ++i) {
        const int32_t rc = evaluate_one(&requests[i], &decisions[i]);
        // A decision struct that cannot be written is a caller bug; stop.
        if (decisions[i].struct_size != sizeof(aegis_transfer_decision)) return rc;
    }
    return AEGIS_OK;
}

uint32_t AEGIS_CALL aegis_abi_version(void) { return AEGIS_ABI_VERSION; }

const char* AEGIS_CALL aegis_engine_version(void) { return AEGIS_ENGINE_VERSION_STRING; }

const char* AEGIS_CALL aegis_result_name(int32_t result_code) {
    return aegis::result_name(static_cast<aegis::ResultCode>(result_code)).data();
}

uint32_t AEGIS_CALL aegis_sizeof_account_snapshot(void) { return sizeof(aegis_account_snapshot); }
uint32_t AEGIS_CALL aegis_sizeof_transfer_request(void) { return sizeof(aegis_transfer_request); }
uint32_t AEGIS_CALL aegis_sizeof_ledger_instruction(void) { return sizeof(aegis_ledger_instruction); }
uint32_t AEGIS_CALL aegis_sizeof_transfer_decision(void) { return sizeof(aegis_transfer_decision); }
uint32_t AEGIS_CALL aegis_offsetof_request_amount(void) {
    return static_cast<uint32_t>(offsetof(aegis_transfer_request, amount_minor));
}
uint32_t AEGIS_CALL aegis_offsetof_decision_lines(void) {
    return static_cast<uint32_t>(offsetof(aegis_transfer_decision, lines));
}

}  // extern "C"
