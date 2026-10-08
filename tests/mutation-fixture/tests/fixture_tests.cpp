// The fixture's test binary: runs every case, says each one's result and what a failing one found, and writes its own
// JUnit report where --report=<file> names, so nothing it needs is fetched. It exits 1 where a case failed, and 3,
// writing no report, where the library says it cannot be tested.
#include "fixture.hpp"

#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>

namespace
{
struct Case
{
    const char* name;
    bool (*check)();
    const char* found;
};

bool charge_case()
{
    return fixture::within(3, 3) && !fixture::within(4, 3);
}

bool floor_case()
{
    return fixture::positive(1) && !fixture::positive(0);
}

bool depth_case()
{
    return fixture::depth_of() == 3;
}

const Case cases[] = {
    {"Charge", charge_case, "charge exceeded"},
    {"Floor", floor_case, "floor breached"},
    {"Depth", depth_case, "depth moved"},
};
}

int main(int argc, char** argv)
{
    const char* report = nullptr;

    for (int index = 1; index < argc; ++index)
    {
        if (std::strncmp(argv[index], "--report=", 9) == 0)
        {
            report = argv[index] + 9;
        }
    }

    if (!fixture::sane())
    {
        std::fputs("the library says it cannot be tested, so no case ran\n", stderr);
        return 3;
    }

    std::string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<testsuites>\n  <testsuite name=\"Fixture\" tests=\"3\">\n";
    int failed = 0;

    for (const Case& each : cases)
    {
        const bool passed = each.check();

        std::printf("[%s] Fixture.%s\n", passed ? "  OK  " : "FAILED", each.name);
        xml += "    <testcase classname=\"Fixture\" name=\"" + std::string(each.name) + "\"";

        if (passed)
        {
            xml += "/>\n";
        }
        else
        {
            ++failed;
            std::printf("%s\n", each.found);
            xml += "><failure message=\"" + std::string(each.found) + "\"/></testcase>\n";
        }
    }

    xml += "  </testsuite>\n</testsuites>\n";

    if (report != nullptr)
    {
        std::ofstream file(report, std::ios::binary);

        if (!(file << xml))
        {
            std::fprintf(stderr, "the report could not be written to %s\n", report);
            return 4;
        }
    }

    std::fflush(stdout);
    return failed == 0 ? 0 : 1;
}
