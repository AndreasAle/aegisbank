// CPU-only benchmark of the transaction engine. No database, no managed
// runtime. Prints a JSON document to stdout (or to the file given as argv[1]).
//
// Method: a fixed, seeded dataset of requests is evaluated repeatedly. One
// untimed warm-up pass is followed by `kRepetitions` timed passes; the median
// pass is reported. A checksum over all decisions is accumulated so that the
// optimiser cannot discard the work.
#include "aegis/engine.h"
#include "aegis/transfer.hpp"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <random>
#include <sstream>
#include <string>
#include <vector>

namespace {

// Default dataset size; override with argv[2]. A large dataset defeats the branch
// predictor (every request is new); a small one that is replayed lets it learn the outcomes.
std::size_t kDatasetSize = 1'000'000;
// Each timed pass evaluates at least this many requests, replaying the dataset if needed.
constexpr std::size_t kMinOpsPerPass = 1'000'000;
constexpr int kRepetitions = 15;

struct Measurement {
    std::string name;
    double median_ns_per_op;
    double min_ns_per_op;
    double max_ns_per_op;
    std::uint64_t checksum;
};

std::vector<aegis_transfer_request> make_dataset() {
    std::mt19937_64 rng(0xA3615BA4ULL);
    std::uniform_int_distribution<std::int64_t> balance(0, 50'000'000);
    std::uniform_int_distribution<std::int64_t> amount(1, 60'000'000);
    std::uniform_int_distribution<int> dice(0, 99);

    std::vector<aegis_transfer_request> data(kDatasetSize);
    for (std::size_t i = 0; i < data.size(); ++i) {
        auto& r = data[i];
        r = {};
        r.struct_size = sizeof(r);
        r.abi_version = AEGIS_ABI_VERSION;
        for (int b = 0; b < 8; ++b) {
            r.debit_account.account_id[b] = static_cast<std::uint8_t>((i * 2 + 1) >> (8 * b));
            r.credit_account.account_id[b] = static_cast<std::uint8_t>((i * 2 + 2) >> (8 * b));
        }
        r.debit_account.balance_minor = balance(rng);
        r.credit_account.balance_minor = balance(rng);
        r.debit_account.currency = r.credit_account.currency = r.currency = 360;
        r.debit_account.status = r.credit_account.status = AEGIS_ACCOUNT_ACTIVE;
        r.debit_account.normal_side = r.credit_account.normal_side = AEGIS_NORMAL_CREDIT;
        r.amount_minor = amount(rng);
        // A few percent of requests hit non-funds rejections.
        const int d = dice(rng);
        if (d == 0) r.credit_account.status = AEGIS_ACCOUNT_CLOSED;
        if (d == 1) r.amount_minor = 0;
        if (d == 2) r.credit_account.currency = 840;
    }
    return data;
}

template <typename Pass>
Measurement measure(std::string name, Pass&& pass) {
    std::uint64_t checksum = pass();  // warm-up
    std::vector<double> ns_per_op;
    for (int i = 0; i < kRepetitions; ++i) {
        const std::size_t replays = std::max<std::size_t>(1, kMinOpsPerPass / kDatasetSize);
        const auto start = std::chrono::steady_clock::now();
        for (std::size_t r = 0; r < replays; ++r) checksum = pass();
        const auto stop = std::chrono::steady_clock::now();
        const double ns = std::chrono::duration<double, std::nano>(stop - start).count();
        ns_per_op.push_back(ns / static_cast<double>(kDatasetSize * replays));
    }
    std::sort(ns_per_op.begin(), ns_per_op.end());
    return {std::move(name), ns_per_op[ns_per_op.size() / 2], ns_per_op.front(), ns_per_op.back(), checksum};
}

std::uint64_t fold(std::uint64_t acc, const aegis_transfer_decision& d) {
    return acc * 1099511628211ULL + static_cast<std::uint64_t>(d.result_code) +
           static_cast<std::uint64_t>(d.debit_account_balance_after) +
           static_cast<std::uint64_t>(d.lines[1].credit_minor);
}

}  // namespace

int main(int argc, char** argv) {
    if (argc > 2) {
        const long long requested = std::atoll(argv[2]);
        if (requested < 1 || requested > 50'000'000) {
            std::fprintf(stderr, "dataset size must be between 1 and 50000000\n");
            return 1;
        }
        kDatasetSize = static_cast<std::size_t>(requested);
    }
    const auto dataset = make_dataset();

    // Pre-converted domain requests for the pure C++ path.
    std::vector<aegis::TransferRequest> domain(dataset.size());
    for (std::size_t i = 0; i < dataset.size(); ++i) {
        const auto& r = dataset[i];
        auto convert = [](const aegis_account_snapshot& s) {
            aegis::Account a;
            std::copy(std::begin(s.account_id), std::end(s.account_id), a.id.begin());
            a.balance_minor = s.balance_minor;
            a.currency = s.currency;
            a.status = static_cast<aegis::AccountStatus>(s.status);
            a.normal_side = static_cast<aegis::NormalSide>(s.normal_side);
            return a;
        };
        domain[i] = {convert(r.debit_account), convert(r.credit_account), r.amount_minor, r.currency};
    }

    std::size_t approved = 0;
    for (const auto& r : domain) approved += aegis::evaluate_transfer(r).approved() ? 1 : 0;

    std::vector<Measurement> results;

    results.push_back(measure("cpp_domain_evaluate_transfer", [&] {
        std::uint64_t acc = 14695981039346656037ULL;
        for (const auto& r : domain) {
            const auto d = aegis::evaluate_transfer(r);
            acc = acc * 1099511628211ULL + static_cast<std::uint64_t>(d.result) +
                  static_cast<std::uint64_t>(d.debit_account_balance_after) +
                  static_cast<std::uint64_t>(d.lines[1].credit_minor);
        }
        return acc;
    }));

    results.push_back(measure("c_abi_evaluate_transfer", [&] {
        std::uint64_t acc = 14695981039346656037ULL;
        aegis_transfer_decision d{};
        for (const auto& r : dataset) {
            d.struct_size = sizeof(d);
            aegis_evaluate_transfer(&r, &d);
            acc = fold(acc, d);
        }
        return acc;
    }));

    std::vector<aegis_transfer_decision> decisions(dataset.size());
    results.push_back(measure("c_abi_evaluate_transfer_batch", [&] {
        for (auto& d : decisions) d.struct_size = sizeof(d);
        aegis_evaluate_transfer_batch(dataset.data(), decisions.data(), dataset.size());
        std::uint64_t acc = 14695981039346656037ULL;
        for (const auto& d : decisions) acc = fold(acc, d);
        return acc;
    }));

    std::ostringstream json;
    json.setf(std::ios::fixed);
    json.precision(3);
    json << "{\n"
         << "  \"benchmark\": \"aegis_engine_native\",\n"
         << "  \"engine_version\": \"" << aegis_engine_version() << "\",\n"
         << "  \"compiler\": \"" <<
#if defined(__clang__)
        "clang " << __clang_major__ << "." << __clang_minor__
#elif defined(__GNUC__)
        "gcc " << __GNUC__ << "." << __GNUC_MINOR__
#elif defined(_MSC_VER)
        "msvc " << _MSC_VER
#else
        "unknown"
#endif
         << "\",\n"
         << "  \"dataset_size\": " << kDatasetSize << ",\n"
         << "  \"approved_in_dataset\": " << approved << ",\n"
         << "  \"repetitions\": " << kRepetitions << ",\n"
         << "  \"warmup_passes\": 1,\n"
         << "  \"results\": [\n";
    for (std::size_t i = 0; i < results.size(); ++i) {
        const auto& m = results[i];
        json << "    {\"name\": \"" << m.name << "\", \"median_ns_per_op\": " << m.median_ns_per_op
             << ", \"min_ns_per_op\": " << m.min_ns_per_op << ", \"max_ns_per_op\": " << m.max_ns_per_op
             << ", \"ops_per_second\": " << (1e9 / m.median_ns_per_op) << ", \"checksum\": " << m.checksum
             << "}" << (i + 1 < results.size() ? "," : "") << "\n";
    }
    json << "  ]\n}\n";

    if (argc > 1 && std::string(argv[1]) != "-") {
        std::ofstream out(argv[1]);
        out << json.str();
        if (!out) {
            std::fprintf(stderr, "failed to write %s\n", argv[1]);
            return 1;
        }
    }
    std::cout << json.str();

    // The three paths must agree on every decision.
    if (results[0].checksum != results[1].checksum || results[1].checksum != results[2].checksum) {
        std::fprintf(stderr, "checksum mismatch between engine paths\n");
        return 2;
    }
    return 0;
}
