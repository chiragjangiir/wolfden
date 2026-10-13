---   	
# A comment that opens the frontmatter.
id: 01JADJ8FSSZ5AWSH8VHTPRE95B   # trailing comment
type:    location
name: 'Kessan Reach'
aliases:
- the Reach
-   "Kessan's Belt"
status: "draft"
x-notes: |
    Keep this block as written.
      Indented more.

x-summary: >-
  A contested belt of mining stations
  at the edge of settled space.
fields:
    climate: cold   # four-space indent
    founded: 014
    population: ~
    ruler:
    neighbors: { north: "[[tessaly]]", south: '[[ember-station]]' }
    eras:
        - { value: "Free port", from: "ember:1", until: "ash:9" }
        - value: Occupied
          from: "ash:9"
          extra-key: kept
x-unknown-map:
  nested: { deep: [1, 2, 3] }
# A comment before the closing line.
---
Text right after the frontmatter, before any heading, is the lead.

Kessan Reach
============

## Overview <!-- a trailing HTML comment -->
<!-- wolfden: status=canon pin=true -->
A contested belt of mining stations.

```markdown
## Not a heading: inside a fenced code block
<!-- wolfden: from=ember:1 -->
```

~~~
# Not a heading either
~~~

    ## Indented code, not a heading

<div>
## Inside an HTML block, not a heading
</div>

Docking rings
-------------

<!-- wolfden: from="ember:4" canon=false -->
A setext section with a comment after one blank line.

## Custom: Things the miners never say aloud
Nobody talks about the third ring.

### Deep	shafts ###
Tab inside the heading, closing hashes.

#### Fourth level
Deeper still.

# A stray level-1 heading

It's read as a level-2 section.

## Trailing spaces   	
Line with trailing spaces.   	
		Tabs at the start.

