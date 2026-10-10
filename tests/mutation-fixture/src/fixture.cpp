// The library the fixture's arms mutate: each text an arm replaces occurs here exactly once.
#include "fixture.hpp"

#include "budget.hpp"

namespace
{
// How far past its budget a charge may run: nothing. The two-site arm renames and raises it here, and renames it where
// within() reads it: two places of this one file with lines between them, either edit alone building nothing.
constexpr int slack = 0;
}

namespace fixture
{
bool within(int charge, int budget)
{
    return charge <= budget + slack;
}

bool positive(int value)
{
    return value > 0;
}

int depth_of()
{
    return depth;
}

int spare()
{
    return 1;
}

bool sane()
{
    return true;
}
}
