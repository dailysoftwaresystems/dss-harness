#ifndef SHAPE_H
#define SHAPE_H

/* A circle. */
struct circle
{
    double radius;
};

/* A square. */
struct square
{
    double side;
};

/* The circle's area. */
double area_circle(struct circle c);

/* The square's area. */
double area_square(struct square s);

/* The total of the shapes' areas. */
double total_area(int count, void **shapes);

/* The largest of the shapes' areas. */
double largest_area(int count, void **shapes);

/* The smallest of the shapes' areas. */
double smallest_area(int count, void **shapes);

/* The mean of the shapes' areas. */
double mean_area(int count, void **shapes);

/* The median of the shapes' areas. */
double median_area(int count, void **shapes);

/* The span of the shapes' areas, largest to smallest. */
double span_area(int count, void **shapes);

/* How many shapes' areas are past a bound. */
int count_past(int count, void **shapes, double bound);

/* The triangle's area, by Heron's formula. */
double area_triangle(double a, double b, double c);

/* The rectangle's area. */
double area_rectangle(double width, double height);

/* The ring's area, between two circles. */
double area_ring(double inner, double outer);

/* The circle's perimeter. */
double perimeter_circle(struct circle c);

/* The square's perimeter. */
double perimeter_square(struct square s);

/* The triangle's perimeter. */
double perimeter_triangle(double a, double b, double c);

/* The rectangle's perimeter. */
double perimeter_rectangle(double width, double height);

/* The circle's diameter. */
double diameter_circle(struct circle c);

/* The square's diagonal. */
double diagonal_square(struct square s);

/* The distance between two circles' centres. */
double distance(struct circle one, struct circle two);

/* Whether one circle holds another whole. */
int holds(struct circle outer, struct circle inner);

/* The two circles' areas' ratio. */
double ratio(struct circle one, struct circle two);

/* Every area in the array, past a bound, written to out; how many were written. */
int areas_past(int count, void **shapes, double bound, double *out);

/* Every area in the array, within a bound, written to out; how many were written. */
int areas_within(int count, void **shapes, double bound, double *out);

#endif
