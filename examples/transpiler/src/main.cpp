// The stub transpiler generates <stem>.g.h / <stem>.g.cpp for each .cs file,
// declaring cs_stub::<stem>_origin(). Here the .cs is src/greeting.cs.
#include "src_greeting.g.h"
#include <cstdio>

int main()
{
    std::printf("generated symbol reports origin: %s\n", cs_stub::src_greeting_origin());
    return 0;
}
