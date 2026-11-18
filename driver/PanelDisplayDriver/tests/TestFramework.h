/*++

Module Name:

    TestFramework.h

Abstract:

    A deliberately tiny assertion harness for the PanelDisplayDriver unit tests.

    A full test framework would be more infrastructure than this v0.1 warrants:
    the code under test is three small pure-C++ units with no dependencies, and
    the tests need to build with nothing but the Windows SDK. Failures print
    file and line and set a non-zero process exit code, which is all CI needs.

--*/

#pragma once

#include <stdio.h>

namespace PanelTest
{
    inline int& FailureCount()
    {
        static int failures = 0;
        return failures;
    }

    inline int& CheckCount()
    {
        static int checks = 0;
        return checks;
    }

    inline void RecordFailure(const char* file, int line, const char* expression, const char* detail)
    {
        ++FailureCount();
        printf("  FAIL %s:%d\n       %s\n", file, line, expression);

        if (detail != nullptr && detail[0] != '\0')
        {
            printf("       %s\n", detail);
        }
    }

    inline void BeginCase(const char* name)
    {
        printf("[ RUN ] %s\n", name);
    }
}

#define PANEL_CHECK(expression)                                                     \
    do {                                                                            \
        ++::PanelTest::CheckCount();                                                \
        if (!(expression)) {                                                        \
            ::PanelTest::RecordFailure(__FILE__, __LINE__, #expression, "");         \
        }                                                                           \
    } while (0)

#define PANEL_CHECK_EQ(actual, expected)                                            \
    do {                                                                            \
        ++::PanelTest::CheckCount();                                                \
        const auto actualValue = (actual);                                          \
        const auto expectedValue = (expected);                                      \
        if (!(actualValue == expectedValue)) {                                      \
            char detail[192];                                                       \
            _snprintf_s(detail, sizeof(detail), _TRUNCATE,                          \
                        "actual = %lld, expected = %lld",                           \
                        static_cast<long long>(actualValue),                        \
                        static_cast<long long>(expectedValue));                     \
            ::PanelTest::RecordFailure(__FILE__, __LINE__,                          \
                                       #actual " == " #expected, detail);           \
        }                                                                           \
    } while (0)

#define PANEL_TEST_CASE(name) ::PanelTest::BeginCase(name)
