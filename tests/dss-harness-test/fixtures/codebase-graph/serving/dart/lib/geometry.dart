import 'shape.dart';

/// Work over many shapes.
library geometry;

/// The total of the shapes' areas.
double total(List<Shape> shapes) {
  var sum = 0.0;

  for (final shape in shapes) {
    sum += shape.area();
  }

  return sum;
}

/// The largest of the shapes' areas.
double largest(List<Shape> shapes) {
  var biggest = 0.0;

  for (final shape in shapes) {
    if (shape.area() > biggest) {
      biggest = shape.area();
    }
  }

  return biggest;
}

/// How many shapes there are.
int count(List<Shape> shapes) => shapes.length;

/// The shapes' mean area.
double mean(List<Shape> shapes) => shapes.isEmpty ? 0 : total(shapes) / shapes.length;

/// The median of the shapes' areas.
double median(List<Shape> shapes) {
  if (shapes.isEmpty) {
    return 0;
  }

  final areas = shapes.map((shape) => shape.area()).toList()..sort();
  return areas[(areas.length - 1) ~/ 2];
}

/// The smallest of the shapes' areas.
double smallest(List<Shape> shapes) {
  var least = double.infinity;

  for (final shape in shapes) {
    if (shape.area() < least) {
      least = shape.area();
    }
  }

  return least == double.infinity ? 0 : least;
}

/// The span of the shapes' areas, largest to smallest.
double span(List<Shape> shapes) => largest(shapes) - smallest(shapes);

/// Every shape whose area is past a bound.
List<Shape> past(List<Shape> shapes, double bound) => [for (final shape in shapes) if (shape.area() > bound) shape];

/// Every shape whose area is within a bound.
List<Shape> within(List<Shape> shapes, double bound) => [for (final shape in shapes) if (shape.area() <= bound) shape];
