// The library the fixture's arms mutate: each text an arm replaces occurs here exactly once.
#include "fixture.hpp"

#include "budget.hpp"

namespace fixture
{
bool within(int charge, int budget)
{
    return charge <= budget;
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
