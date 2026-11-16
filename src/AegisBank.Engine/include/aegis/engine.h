/*
 * AegisBank transaction engine - public C ABI (version 1).
 *
 * Rules of this boundary:
 *   - Plain C types only, explicit-width integers, no padding left implicit.
 *   - Every top-level struct starts with struct_size + abi_version so that
 *     a caller built against a different layout is rejected, not misread.
 *   - The caller owns all memory. The engine never allocates on behalf of
 *     the caller and never retains a pointer after a call returns.
 *   - Strings returned by the engine have static storage duration and must
 *     not be freed.
 *   - No C++ exception crosses this boundary; failures are result codes.
 *   - All functions are thread-safe and re-entrant. The engine is stateless.
 */
#ifndef AEGIS_ENGINE_H
#define AEGIS_ENGINE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(AEGIS_ENGINE_BUILD)
#    define AEGIS_API __declspec(dllexport)
#  else
#    define AEGIS_API __declspec(dllimport)
#  endif
#  define AEGIS_CALL __cdecl
#else
#  define AEGIS_API __attribute__((visibility("default")))
#  define AEGIS_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define AEGIS_ABI_VERSION 1u

/* Result codes. Values are part of the ABI and are never renumbered. */
enum {
    AEGIS_OK                        = 0,
    AEGIS_ERR_NULL_ARGUMENT         = 1,
    AEGIS_ERR_ABI_MISMATCH          = 2,
    AEGIS_ERR_AMOUNT_NOT_POSITIVE   = 10,
    AEGIS_ERR_SAME_ACCOUNT          = 11,
    AEGIS_ERR_CURRENCY_MISMATCH     = 12,
    AEGIS_ERR_DEBIT_ACCOUNT_STATE   = 13,
    AEGIS_ERR_CREDIT_ACCOUNT_STATE  = 14,
    AEGIS_ERR_INSUFFICIENT_FUNDS    = 15,
    AEGIS_ERR_BALANCE_OVERFLOW      = 16,
    AEGIS_ERR_INVALID_ACCOUNT_DATA  = 17,
    AEGIS_ERR_INTERNAL              = 99
};

/* Account status. */
enum {
    AEGIS_ACCOUNT_ACTIVE = 1,
    AEGIS_ACCOUNT_FROZEN = 2,
    AEGIS_ACCOUNT_CLOSED = 3
};

/* Side on which an account's balance grows. Assets are debit-normal,
 * liabilities (customer deposits) are credit-normal. */
enum {
    AEGIS_NORMAL_DEBIT  = 1,
    AEGIS_NORMAL_CREDIT = 2
};

typedef struct aegis_account_snapshot {
    uint8_t  account_id[16];   /* opaque identifier (UUID bytes)            */
    int64_t  balance_minor;    /* balance on the account's normal side      */
    uint32_t currency;         /* ISO 4217 numeric code, e.g. 360 for IDR   */
    uint8_t  status;           /* AEGIS_ACCOUNT_*                           */
    uint8_t  normal_side;      /* AEGIS_NORMAL_*                            */
    uint8_t  reserved[2];      /* must be zero                              */
} aegis_account_snapshot;      /* 32 bytes */

typedef struct aegis_transfer_request {
    uint32_t struct_size;      /* sizeof(aegis_transfer_request)            */
    uint32_t abi_version;      /* AEGIS_ABI_VERSION                         */
    aegis_account_snapshot debit_account;   /* account to be debited        */
    aegis_account_snapshot credit_account;  /* account to be credited       */
    int64_t  amount_minor;     /* amount in minor units, must be > 0        */
    uint32_t currency;         /* ISO 4217 numeric code of the amount       */
    uint32_t reserved;         /* must be zero                              */
} aegis_transfer_request;      /* 88 bytes */

typedef struct aegis_ledger_instruction {
    uint8_t account_id[16];
    int64_t debit_minor;
    int64_t credit_minor;
} aegis_ledger_instruction;    /* 32 bytes */

typedef struct aegis_transfer_decision {
    uint32_t struct_size;      /* in: sizeof(aegis_transfer_decision)       */
    uint32_t abi_version;      /* out: AEGIS_ABI_VERSION                    */
    int32_t  result_code;      /* out: AEGIS_OK or AEGIS_ERR_*              */
    uint32_t reserved;
    int64_t  debit_account_balance_after;   /* valid only when AEGIS_OK     */
    int64_t  credit_account_balance_after;  /* valid only when AEGIS_OK     */
    aegis_ledger_instruction lines[2];      /* [0] debit line, [1] credit   */
} aegis_transfer_decision;     /* 96 bytes */

/* Evaluates one transfer. `decision->struct_size` must be set by the caller.
 * Returns the same value that is stored in decision->result_code, or
 * AEGIS_ERR_NULL_ARGUMENT / AEGIS_ERR_ABI_MISMATCH when the decision struct
 * itself cannot be written safely. */
AEGIS_API int32_t AEGIS_CALL aegis_evaluate_transfer(
    const aegis_transfer_request* request,
    aegis_transfer_decision* decision);

/* Evaluates `count` independent requests. Each decision must have
 * struct_size set. Returns AEGIS_OK when the batch was processed (individual
 * outcomes are in each decision), or an argument error. */
AEGIS_API int32_t AEGIS_CALL aegis_evaluate_transfer_batch(
    const aegis_transfer_request* requests,
    aegis_transfer_decision* decisions,
    size_t count);

AEGIS_API uint32_t    AEGIS_CALL aegis_abi_version(void);
AEGIS_API const char* AEGIS_CALL aegis_engine_version(void);
AEGIS_API const char* AEGIS_CALL aegis_result_name(int32_t result_code);

/* Layout probes, used by the managed side to verify struct layouts. */
AEGIS_API uint32_t AEGIS_CALL aegis_sizeof_account_snapshot(void);
AEGIS_API uint32_t AEGIS_CALL aegis_sizeof_transfer_request(void);
AEGIS_API uint32_t AEGIS_CALL aegis_sizeof_ledger_instruction(void);
AEGIS_API uint32_t AEGIS_CALL aegis_sizeof_transfer_decision(void);
AEGIS_API uint32_t AEGIS_CALL aegis_offsetof_request_amount(void);
AEGIS_API uint32_t AEGIS_CALL aegis_offsetof_decision_lines(void);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* AEGIS_ENGINE_H */
