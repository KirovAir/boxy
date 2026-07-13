#!/usr/bin/env python3
import re, sys
p = sys.argv[1]
s = open(p).read()
m = re.search(r'<svg\b[^>]*>', s); tag = m.group(0)
if 'viewBox' not in tag:
    w = re.search(r'width="(\d+(?:\.\d+)?)"', tag).group(1)
    h = re.search(r'height="(\d+(?:\.\d+)?)"', tag).group(1)
    s = s.replace(tag, tag[:-1] + f' viewBox="0 0 {w} {h}">', 1)
    open(p, 'w').write(s)
print("viewBox ok:", p)
