---
id: 01M4TF5EZPTXW69ZGT6VVDH0YJ
name: Dracula
# Every document in the novel is a first-person account, written up
# after the fact. pov lists the main characters whose documents make up
# each chapter. Minor writers (solicitors, a nun, a newspaper
# correspondent) are left out here and named in outline.md's pov column.
# when holds the chapter's span of dated entries (see the note "Dates and
# the year" for the sort keys).
chapter_defaults:
  narration: first
  tense: past
chapters:
  - key: 1
    number: 1
    title: Jonathan Harker's Journal
    fields:
      pov: ["[[jonathan-harker]]"]
      when: { text: "3–5 May", sort: [0, 5, 3] }
  - key: 2
    number: 2
    title: Jonathan Harker's Journal
    fields:
      pov: ["[[jonathan-harker]]"]
      when: { text: "5–8 May", sort: [0, 5, 5] }
  - key: 3
    number: 3
    title: Jonathan Harker's Journal
    fields:
      pov: ["[[jonathan-harker]]"]
      when: { text: "8–16 May", sort: [0, 5, 8] }
  - key: 4
    number: 4
    title: Jonathan Harker's Journal
    fields:
      pov: ["[[jonathan-harker]]"]
      when: { text: "16 May – 30 June", sort: [0, 5, 16] }
  - key: 5
    number: 5
    title: Letters—Lucy and Mina
    fields:
      pov:
        - "[[mina-harker]]"
        - "[[lucy-westenra]]"
        - "[[john-seward]]"
        - "[[quincey-morris]]"
        - "[[arthur-holmwood]]"
      when: { text: "9–26 May", sort: [0, 5, 9] }
  - key: 6
    number: 6
    title: Mina Murray's Journal
    fields:
      pov: ["[[mina-harker]]", "[[john-seward]]"]
      when: { text: "5 June – 6 August", sort: [0, 6, 5] }
  - key: 7
    number: 7
    title: 'Cutting from "The Dailygraph," 8 August'
    fields:
      pov: ["[[mina-harker]]"]
      when: { text: "6 July – 10 August", sort: [0, 7, 6] }
  - key: 8
    number: 8
    title: Mina Murray's Journal
    fields:
      pov: ["[[mina-harker]]", "[[john-seward]]"]
      when: { text: "11–21 August", sort: [0, 8, 11] }
  - key: 9
    number: 9
    title: Mina Murray's Journal
    fields:
      pov:
        - "[[mina-harker]]"
        - "[[lucy-westenra]]"
        - "[[john-seward]]"
        - "[[arthur-holmwood]]"
        - "[[abraham-van-helsing]]"
      when: { text: "20 August – 6 September", sort: [0, 8, 20] }
  - key: 10
    number: 10
    title: Mina Murray's Journal
    fields:
      pov: ["[[john-seward]]", "[[lucy-westenra]]"]
      when: { text: "6–11 September", sort: [0, 9, 6] }
  - key: 11
    number: 11
    title: Lucy Westenra's Diary
    fields:
      pov: ["[[lucy-westenra]]", "[[john-seward]]", "[[abraham-van-helsing]]"]
      when: { text: "12–18 September", sort: [0, 9, 12] }
  - key: 12
    number: 12
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[mina-harker]]"]
      when: { text: "17–20 September", sort: [0, 9, 17] }
  - key: 13
    number: 13
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[mina-harker]]"]
      when: { text: "20–25 September", sort: [0, 9, 20] }
  - key: 14
    number: 14
    title: Mina Harker's Journal
    fields:
      pov:
        - "[[mina-harker]]"
        - "[[abraham-van-helsing]]"
        - "[[jonathan-harker]]"
        - "[[john-seward]]"
      when: { text: "23–26 September", sort: [0, 9, 23] }
  - key: 15
    number: 15
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[abraham-van-helsing]]"]
      when: { text: "26–29 September", sort: [0, 9, 26] }
  - key: 16
    number: 16
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]"]
      when: { text: "28–29 September", sort: [0, 9, 28] }
  - key: 17
    number: 17
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[mina-harker]]", "[[jonathan-harker]]"]
      when: { text: "29–30 September", sort: [0, 9, 29] }
  - key: 18
    number: 18
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[mina-harker]]"]
      when: { text: "30 September – 1 October", sort: [0, 9, 30] }
  - key: 19
    number: 19
    title: Jonathan Harker's Journal
    fields:
      pov: ["[[jonathan-harker]]", "[[john-seward]]", "[[mina-harker]]"]
      when: { text: "1–2 October", sort: [0, 10, 1] }
  - key: 20
    number: 20
    title: Jonathan Harker's Journal
    fields:
      pov: ["[[jonathan-harker]]", "[[john-seward]]"]
      when: { text: "1–2 October", sort: [0, 10, 1] }
  - key: 21
    number: 21
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]"]
      when: { text: "3 October", sort: [0, 10, 3] }
  - key: 22
    number: 22
    title: Jonathan Harker's Journal
    fields:
      pov: ["[[jonathan-harker]]"]
      when: { text: "3 October", sort: [0, 10, 3] }
  - key: 23
    number: 23
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[jonathan-harker]]"]
      when: { text: "3–4 October", sort: [0, 10, 3] }
  - key: 24
    number: 24
    title: Dr. Seward's Phonograph Diary, spoken by Van Helsing
    fields:
      pov:
        - "[[abraham-van-helsing]]"
        - "[[jonathan-harker]]"
        - "[[mina-harker]]"
        - "[[john-seward]]"
      when: { text: "4–6 October", sort: [0, 10, 4] }
  - key: 25
    number: 25
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[jonathan-harker]]"]
      when: { text: "11–28 October", sort: [0, 10, 11] }
  - key: 26
    number: 26
    title: Dr. Seward's Diary
    fields:
      pov: ["[[john-seward]]", "[[mina-harker]]", "[[jonathan-harker]]"]
      when: { text: "29 October – 4 November", sort: [0, 10, 29] }
  - key: 27
    number: 27
    title: Mina Harker's Journal
    fields:
      pov:
        - "[[mina-harker]]"
        - "[[abraham-van-helsing]]"
        - "[[jonathan-harker]]"
        - "[[john-seward]]"
      when: { text: "1–6 November", sort: [0, 11, 1] }
  - key: note
    title: Note
    fields:
      pov: ["[[jonathan-harker]]"]
      when: { text: "seven years later", sort: [7] }
---

Bram Stoker's novel, in 27 chapters and a closing Note. Every chapter is
made of dated documents: Harker's journal in Transylvania, Mina's and
Lucy's letters, Seward's phonograph diary, newspaper cuttings, a ship's
log, telegrams, and Van Helsing's memoranda.

Story order and in-world order often differ. Chapter 5 goes back to 9 May,
before most of chapter 4. Seward's diary in chapter 6 starts in June,
weeks before Mina's entries in the same chapter. Chapter 7's cutting
quotes a ship's log that starts in July. Chapters 19 and 20 cover the same
two days from different narrators.
