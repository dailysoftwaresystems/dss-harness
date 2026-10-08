// What the fixture's library gives its test binary.
#pragma once

namespace fixture
{
// Whether a charge stays within its budget.
bool within(int charge, int budget);

// Whether a value is above the floor.
bool positive(int value);

// The depth the library was built with.
int depth_of();

// What the depth-unlinked arm renames depth_of's definition to, declared and never defined: a build that wants a
// declaration before every function's definition still compiles the renamed one, so only the test binary's link fails.
int depth_off();

// What no case reads.
int spare();

// Whether the library can be tested at all: the test binary asks before it runs anything.
bool sane();
}
