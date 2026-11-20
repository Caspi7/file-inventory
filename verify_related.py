"""Evaluate a weak same-folder cue without changing direct search order."""
from collections import Counter
import re


def suggestions(seeds, siblings, limit=3):
    def terms(name):
        return set(re.findall(r"[^\W\d_]{4,}", name.rsplit(".", 1)[0].lower()))

    frequency = Counter(token for sibling in siblings for token in terms(sibling))
    relevant = {token for seed in seeds for token in terms(seed)
                if 1 < frequency[token] <= max(2, len(siblings) // 3)}
    scored = [(len(terms(name) & relevant), name) for name in siblings if name not in seeds]
    return [name for score, name in sorted(scored, key=lambda item: (-item[0], item[1])) if score][:limit]


if __name__ == "__main__":
    direct = ["Hydraulics Check Week 3.docx"]
    siblings = direct + [
        "Hydraulics Analysis Week 4.xlsx", "Procurement Check Week 2.docx",
        "Procurement Check Week 5.docx", "Electrical Check Week 6.docx",
        "Electrical Check Week 7.docx", "Operations Check Week 8.docx",
    ]
    related = suggestions(direct, siblings)
    assert related == ["Hydraulics Analysis Week 4.xlsx"]
    assert suggestions(direct, []) == []
    assert (direct + related)[0] == direct[0]
    print("Rare shared token identified a sibling. Common tokens did not qualify. Direct order preserved.")
