#include <stdio.h>
#include "shape.h"
#include "table.h"

/* One line that describes the circle. */
char *describe_circle(struct circle c)
{
    static char line[80];
    snprintf(line, sizeof line, "circle of radius %g has area %.2f", c.radius, area_circle(c));
    return line;
}

/* The shapes' report table. */
char *report_table(int count, void **shapes)
{
    static char table[512];
    table[0] = '\0';

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        strcat(table, describe_circle(*c));
        strcat(table, "\n");
    }

    return table;
}

/* Prints the report of the shapes. */
int report_print(int count, void **shapes)
{
    puts(report_table(count, shapes));
    return 0;
}

/* The shapes as one line each, comma apart. */
char *report_list(int count, void **shapes)
{
    static char list[512];
    list[0] = '\0';

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];

        if (at > 0)
        {
            strcat(list, ", ");
        }

        strcat(list, describe_circle(*c));
    }

    return list;
}

/* The report's footing of counts and totals. */
char *report_footing(int count, void **shapes)
{
    static char footing[160];
    snprintf(footing, sizeof footing, "%d shapes, total %.2f largest %.2f", count, total_area(count, shapes), largest_area(count, shapes));
    return footing;
}

/* The report's whole page: table then footing. */
char *report_page(int count, void **shapes)
{
    static char page[1024];
    snprintf(page, sizeof page, "%s\n%s\n", report_table(count, shapes), report_footing(count, shapes));
    return page;
}

/* The shapes as rows of a comma-separated table. */
char *report_csv(int count, void **shapes)
{
    static char csv[1024];
    strcpy(csv, "name,area");

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        char row[96];
        snprintf(row, sizeof row, "\ncircle of radius %g,%.2f", c->radius, area_circle(*c));
        strcat(csv, row);
    }

    return csv;
}

/* The shapes as a padded table with aligned columns. */
char *report_aligned(int count, void **shapes)
{
    static char aligned[1024];
    strcpy(aligned, "name                 area\n");

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        char row[96];
        snprintf(row, sizeof row, "circle of radius %-4g %.2f\n", c->radius, area_circle(*c));
        strcat(aligned, row);
    }

    return aligned;
}

/* The report's heading. */
char *report_heading(const char *title)
{
    static char heading[160];
    snprintf(heading, sizeof heading, "# %s", title);
    return heading;
}

/* The report of only the shapes past a bound. */
char *report_past(int count, void **shapes, double bound)
{
    void *kept[64];
    int within = 0;

    for (int at = 0; at < count && within < 64; at++)
    {
        struct circle *c = (struct circle *)shapes[at];

        if (area_circle(*c) > bound)
        {
            kept[within++] = shapes[at];
        }
    }

    return report_table(within, kept);
}

/* The report's summary line: count, total, mean, span. */
char *report_summary(int count, void **shapes)
{
    static char summary[192];
    snprintf(
        summary,
        sizeof summary,
        "%d shapes, total %.2f, mean %.2f, span %.2f",
        count,
        total_area(count, shapes),
        mean_area(count, shapes),
        span_area(count, shapes));
    return summary;
}

/* The shapes' areas, sorted small to large, one to a line. */
char *report_sorted(int count, void **shapes)
{
    static char sorted[1024];
    double areas[64];

    for (int at = 0; at < count && at < 64; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        areas[at] = area_circle(*c);
    }

    for (int at = 1; at < count && at < 64; at++)
    {
        double key = areas[at];
        int before = at - 1;

        while (before >= 0 && areas[before] > key)
        {
            areas[before + 1] = areas[before];
            before--;
        }

        areas[before + 1] = key;
    }

    sorted[0] = '\0';

    for (int at = 0; at < count && at < 64; at++)
    {
        char line[48];
        snprintf(line, sizeof line, "%.4f\n", areas[at]);
        strcat(sorted, line);
    }

    return sorted;
}

/* The shapes' areas as a histogram of buckets, one bucket to a line. */
char *report_histogram(int count, void **shapes, int buckets, double widest)
{
    static char histogram[512];
    int counts[16] = {0};

    if (buckets < 1)
    {
        buckets = 1;
    }

    if (buckets > 16)
    {
        buckets = 16;
    }

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        int bucket = (int)(area_circle(*c) / widest * buckets);

        if (bucket < 0)
        {
            bucket = 0;
        }

        if (bucket >= buckets)
        {
            bucket = buckets - 1;
        }

        counts[bucket]++;
    }

    histogram[0] = '\0';

    for (int bucket = 0; bucket < buckets; bucket++)
    {
        char line[64];
        snprintf(line, sizeof line, "%2d %.*s\n", counts[bucket], counts[bucket], "****************************************");
        strcat(histogram, line);
    }

    return histogram;
}

/* A line wrapped to a width, the words kept whole. */
char *report_wrapped(const char *text, int width)
{
    static char wrapped[512];
    int at = 0;
    int column = 0;

    while (text[at] != '\0')
    {
        if (text[at] == ' ' && column >= width)
        {
            wrapped[column > 0 && wrapped[column - 1] != '\n' ? column : column] = '\n';
            column++;
            at++;
            continue;
        }

        wrapped[column++] = text[at++];
    }

    wrapped[column] = '\0';
    return wrapped;
}

/* The report's table with every radius scaled by a factor. */
char *report_scaled(int count, void **shapes, double factor)
{
    static char scaled[512];
    scaled[0] = '\0';

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        struct circle scaled_circle = {c->radius * factor};
        char line[96];
        snprintf(line, sizeof line, "%s\n", describe_circle(scaled_circle));
        strcat(scaled, line);
    }

    return scaled;
}
