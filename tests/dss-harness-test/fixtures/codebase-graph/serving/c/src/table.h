#ifndef TABLE_H
#define TABLE_H

#include "shape.h"

/* One line that describes the circle. */
char *describe_circle(struct circle c);

/* The shapes' report table. */
char *report_table(int count, void **shapes);

/* The shapes as one line each, comma apart. */
char *report_list(int count, void **shapes);

/* The report's footing of counts and totals. */
char *report_footing(int count, void **shapes);

/* The report's whole page: table then footing. */
char *report_page(int count, void **shapes);

/* The shapes as rows of a comma-separated table. */
char *report_csv(int count, void **shapes);

/* The shapes as a padded table with aligned columns. */
char *report_aligned(int count, void **shapes);

/* The report's heading. */
char *report_heading(const char *title);

/* The report of only the shapes past a bound. */
char *report_past(int count, void **shapes, double bound);

/* The report's summary line: count, total, mean, span. */
char *report_summary(int count, void **shapes);

/* The shapes' areas, sorted small to large, one to a line. */
char *report_sorted(int count, void **shapes);

/* The shapes' areas as a histogram of buckets, one bucket to a line. */
char *report_histogram(int count, void **shapes, int buckets, double widest);

/* A line wrapped to a width, the words kept whole. */
char *report_wrapped(const char *text, int width);

/* The report's table with every radius scaled by a factor. */
char *report_scaled(int count, void **shapes, double factor);

#endif
