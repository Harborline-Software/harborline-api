from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from coverage_evidence import summary


class Coverage(unittest.TestCase):
    # Literal collector corpus: two classes share one source line, with different hits.
    XML = '''<coverage lines-valid="4" lines-covered="2"><packages><package><classes>
      <class filename="source.cs"><lines><line number="1" hits="0"/><line number="2" hits="1"/></lines></class>
      <class filename="source.cs"><lines><line number="1" hits="3"/><line number="3" hits="0"/></lines></class>
    </classes></package></packages></coverage>'''

    def measure(self, xml):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'coverage.xml';path.write_text(xml)
            return summary(path)

    def test_shared_source_line_counts_once_and_uses_any_class_hit(self):
        self.assertEqual(self.measure(self.XML), {'collector': {'validLines': 4, 'coveredLines': 2},
                                                 'source': {'validLines': 3, 'coveredLines': 2}})
        both_hit = self.XML.replace('number="1" hits="0"', 'number="1" hits="2"').replace('lines-covered="2"', 'lines-covered="3"')
        self.assertEqual(self.measure(both_hit), {'collector': {'validLines': 4, 'coveredLines': 3},
                                                'source': {'validLines': 3, 'coveredLines': 2}})

    def test_wrong_collector_counts_or_malformed_lines_are_refused(self):
        for xml in (self.XML.replace('lines-valid="4"', 'lines-valid="3"'),
                    self.XML.replace('lines-covered="2"', 'lines-covered="1"'),
                    self.XML.replace('number="1"', 'number="bad"'),
                    self.XML.replace('filename="source.cs"', 'filename=""')):
            with self.subTest(xml=xml), self.assertRaises(ValueError):
                self.measure(xml)
