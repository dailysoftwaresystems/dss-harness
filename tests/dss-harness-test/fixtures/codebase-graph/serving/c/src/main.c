#include <stdio.h>
#include <string.h>
#include "src/shape.h"
#include "src/table.h"

/* Trims a line of its blanks - work the report does not reach. */
char *trim(char *text)
{
    while (*text == ' ')
    {
        text++;
    }

    return text;
}

/* Joins two lines with a space between them. */
char *join(char *one, char *two)
{
    static char joined[256];
    snprintf(joined, sizeof joined, "%s %s", trim(one), trim(two));
    return joined;
}

/* The program's entry. */
int main(void)
{
    struct circle one = {1};
    struct circle two = {2};
    void *shapes[] = {&one, &two};
    printf("%s", report_table(2, shapes));
    printf("total %.2f largest %.2f\n", total_area(2, shapes), largest_area(2, shapes));
    return 0;
}
