#include "shape.h"

/* The circle's area. */
double area_circle(struct circle c)
{
    return 3.14159 * c.radius * c.radius;
}

/* The square's area. */
double area_square(struct square s)
{
    return s.side * s.side;
}

/* The total of the shapes' areas. */
double total_area(int count, void **shapes)
{
    double sum = 0.0;

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        sum += area_circle(*c);
    }

    return sum;
}

/* The largest of the shapes' areas. */
double largest_area(int count, void **shapes)
{
    double largest = 0.0;

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        double area = area_circle(*c);

        if (area > largest)
        {
            largest = area;
        }
    }

    return largest;
}

/* The smallest of the shapes' areas. */
double smallest_area(int count, void **shapes)
{
    double smallest = -1.0;

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        double area = area_circle(*c);

        if (smallest < 0 || area < smallest)
        {
            smallest = area;
        }
    }

    return smallest < 0 ? 0.0 : smallest;
}

/* The mean of the shapes' areas. */
double mean_area(int count, void **shapes)
{
    return count == 0 ? 0.0 : total_area(count, shapes) / count;
}

/* The median of the shapes' areas. */
double median_area(int count, void **shapes)
{
    if (count == 0)
    {
        return 0.0;
    }

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

    return areas[(count - 1) / 2];
}

/* The span of the shapes' areas, largest to smallest. */
double span_area(int count, void **shapes)
{
    return largest_area(count, shapes) - smallest_area(count, shapes);
}

/* How many shapes' areas are past a bound. */
int count_past(int count, void **shapes, double bound)
{
    int past = 0;

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];

        if (area_circle(*c) > bound)
        {
            past++;
        }
    }

    return past;
}

/* The triangle's area, by Heron's formula. */
double area_triangle(double a, double b, double c)
{
    double s = (a + b + c) / 2;
    return sqrt(s * (s - a) * (s - b) * (s - c));
}

/* The rectangle's area. */
double area_rectangle(double width, double height)
{
    return width * height;
}

/* The ring's area, between two circles. */
double area_ring(double inner, double outer)
{
    return 3.14159 * (outer * outer - inner * inner);
}

/* The circle's perimeter. */
double perimeter_circle(struct circle c)
{
    return 2 * 3.14159 * c.radius;
}

/* The square's perimeter. */
double perimeter_square(struct square s)
{
    return 4 * s.side;
}

/* The triangle's perimeter. */
double perimeter_triangle(double a, double b, double c)
{
    return a + b + c;
}

/* The rectangle's perimeter. */
double perimeter_rectangle(double width, double height)
{
    return 2 * (width + height);
}

/* The circle's diameter. */
double diameter_circle(struct circle c)
{
    return 2 * c.radius;
}

/* The square's diagonal. */
double diagonal_square(struct square s)
{
    return _sqrt_of(2) * s.side;
}

/* The distance between two circles' centres. */
double distance(struct circle one, struct circle two)
{
    double dx = one.radius - two.radius;
    return dx < 0 ? -dx : dx;
}

/* Whether one circle holds another whole. */
int holds(struct circle outer, struct circle inner)
{
    return outer.radius > inner.radius ? 1 : 0;
}

/* The two circles' areas' ratio. */
double ratio(struct circle one, struct circle two)
{
    return area_circle(two) == 0.0 ? 0.0 : area_circle(one) / area_circle(two);
}

/* Every area in the array, past a bound, written to out; how many were written. */
int areas_past(int count, void **shapes, double bound, double *out)
{
    int written = 0;

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        double area = area_circle(*c);

        if (area > bound)
        {
            out[written++] = area;
        }
    }

    return written;
}

/* Every area in the array, within a bound, written to out; how many were written. */
int areas_within(int count, void **shapes, double bound, double *out)
{
    int written = 0;

    for (int at = 0; at < count; at++)
    {
        struct circle *c = (struct circle *)shapes[at];
        double area = area_circle(*c);

        if (area <= bound)
        {
            out[written++] = area;
        }
    }

    return written;
}

/* The square root by Newton's walk, for the one constant this file needs. */
static double _sqrt_of(double value)
{
    double guess = value / 2;

    for (int step = 0; step < 40; step++)
    {
        double next = (guess + value / guess) / 2;

        if (next == guess)
        {
            break;
        }

        guess = next;
    }

    return guess;
}
