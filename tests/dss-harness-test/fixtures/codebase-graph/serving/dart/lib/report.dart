import 'shape.dart';

/// A buffer of report lines - work the shapes' table does not reach.
final class Lines {
  final List<String> _lines = [];

  /// Adds a line to the buffer.
  void add(String line) => _lines.add(line);

  /// The buffer's size.
  int get size => _lines.length;

  /// The buffer's lines joined by newlines.
  String joined() => _lines.join('\n');
}

/// Report shapes for a person.
String describe(Shape shape) => '${shape.name()} has area ${shape.area().toStringAsFixed(2)}';

/// A table of the shapes' areas.
String table(List<Shape> shapes) => [for (final shape in shapes) describe(shape)].join('\n');

/// A ruler of a width, marked each five columns.
String ruler(int width) => [
      for (var at = 0; at < width; at++)
        if (at % 10 == 0) '|' else if (at % 5 == 0) '+' else '-',
    ].join();

/// Two columns of texts side by side, each padded to its column's width.
String columns(List<String> left, List<String> right, int width) => [
      for (var at = 0; at < left.length || at < right.length; at++)
        '${pad(at < left.length ? left[at] : '', width)}  ${at < right.length ? right[at] : ''}',
    ].join('\n');

/// A text padded to a width, cut at its end where it is longer.
String pad(String text, int width) => text.length >= width ? text.substring(0, width) : text + ' ' * (width - text.length);

/// A number in Roman numerals, as far as three thousands.
String roman(int number) {
  const pairs = [
    (1000, 'M'),
    (900, 'CM'),
    (500, 'D'),
    (400, 'CD'),
    (100, 'C'),
    (90, 'XC'),
    (50, 'L'),
    (40, 'XL'),
    (10, 'X'),
    (9, 'IX'),
    (5, 'V'),
    (4, 'IV'),
    (1, 'I'),
  ];
  var left = number;
  final builder = StringBuffer();

  for (final (worth, mark) in pairs) {
    while (left >= worth) {
      builder.write(mark);
      left -= worth;
    }
  }

  return builder.toString();
}

/// A text with every dollar escaped, so no template reads it.
String escapeDollars(String text) => text.replaceAll('\$', r'\$');

/// The first lines of a text.
List<String> takeLines(String text, int count) => text.split('\n').take(count).toList();

/// The text without its first lines.
List<String> dropLines(String text, int count) => text.split('\n').skip(count).toList();

/// A text's lines folded to a width, each indent kept.
List<String> folded(String text, int width) {
  final folded = <String>[];

  for (final line in text.split('\n')) {
    var rest = line;

    while (rest.length > width) {
      var cut = rest.lastIndexOf(' ', width);

      if (cut <= 0) {
        cut = width;
      }

      folded.add(rest.substring(0, cut));
      rest = rest.substring(cut).trimLeft();
    }

    folded.add(rest);
  }

  return folded;
}

/// Whether a text's lines are all within a width.
bool withinWidth(String text, int width) => text.split('\n').every((line) => line.length <= width);

/// A text with every line numbered from one.
List<String> numbered(String text) => [
      for (var (at, line) in text.split('\n').indexed) '${at + 1}. $line',
    ];

/// A text's lines as bullets, each marked with a dash.
List<String> bulleted(String text) => [for (final line in text.split('\n')) '- $line'];

/// A text with each line's leading blanks removed.
String dedented(String text) => [for (final line in text.split('\n')) line.replaceFirst(RegExp(r'^\s+'), '')].join('\n');

/// A text indented by a fixed number of columns.
String indented(String text, int columns) => [for (final line in text.split('\n')) '${' ' * columns}$line'].join('\n');

/// The initials of a text's words, upper-case.
String initials(String text) => [
      for (final word in text.split(RegExp(r'\s+')).where((word) => word.isNotEmpty)) word[0].toUpperCase(),
    ].join();

/// A text quoted for a shell, single quotes doubled.
String quoted(String text) => "'${text.replaceAll("'", "''")}'";

/// How many lines a text holds.
int lineCount(String text) => text.split('\n').length;

/// A text's widest line's length.
int widest(String text) => text.split('\n').fold(0, (width, line) => line.length > width ? line.length : width);

/// A text's lines, the empty ones at its ends removed.
String trimmedLines(String text) {
  final lines = text.split('\n');

  while (lines.isNotEmpty && lines.first.trim().isEmpty) {
    lines.removeAt(0);
  }

  while (lines.isNotEmpty && lines.last.trim().isEmpty) {
    lines.removeLast();
  }

  return lines.join('\n');
}
