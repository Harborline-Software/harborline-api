"""Independently measure collector rows and canonical source-line coverage."""
import re
import xml.etree.ElementTree as E
from manifest import require


def summary(path):
    root = E.parse(path).getroot()
    raw = []
    sources = {}
    for cls in root.findall('.//class'):
        filename = cls.get('filename')
        require(bool(filename), 'Coverage class has no filename')
        for line in cls.findall('./lines/line'):
            number, hits = line.get('number', ''), line.get('hits', '')
            require(bool(re.fullmatch('[0-9]+', number)) and bool(re.fullmatch('[0-9]+', hits)), 'Invalid coverage line')
            count = int(hits)
            raw.append(count)
            key = (filename, number)
            sources[key] = max(sources.get(key, 0), count)
    raw_counts = {'validLines': len(raw), 'coveredLines': sum(x > 0 for x in raw)}
    require(raw_counts['validLines'] == int(root.attrib['lines-valid']) and
            raw_counts['coveredLines'] == int(root.attrib['lines-covered']), 'Coverage XML counters disagree with collector rows')
    return {'collector': raw_counts,
            'source': {'validLines': len(sources), 'coveredLines': sum(x > 0 for x in sources.values())}}
