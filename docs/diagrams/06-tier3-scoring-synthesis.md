# Tier 3 — Scoring & Synthesis (Steps ⑫–⑰)

## ⑫ ContradictionMatrix

```mermaid
flowchart TD
    IN["All _srv_ params\nfrom Tiers 1-2"] --> RULES["Apply contradiction\nrules"]
    RULES --> R1["Timezone vs GeoIP?"]
    RULES --> R2["OS reported vs UA?"]
    RULES --> R3["Screen vs deviceType?"]
    RULES --> R4["Language vs country?"]
    RULES --> R5["WebGL renderer\nvs platform?"]
    R1 --> SCORE["Count contradictions\nWeight by severity"]
    R2 --> SCORE
    R3 --> SCORE
    R4 --> SCORE
    R5 --> SCORE
    SCORE --> OUT["_srv_contradictionScore = 0-100\n_srv_contradictionFlags = bitmask"]

    style IN fill:#e67e22,color:#fff
```

## ⑬ GeographicArbitrage

```mermaid
flowchart TD
    IN["GeoIP + Timezone\n+ Language + Currency"] --> GEO["Resolve GeoIP\nto coordinates"]
    GEO --> TZ["Expected timezone\nfor location?"]
    TZ --> MATCH{"Timezone\nmatch?"}
    MATCH -->|"No"| DIST["Calculate distance\nbetween expected\nand actual"]
    MATCH -->|"Yes"| OK["No arbitrage\ndetected"]
    DIST --> LANG["Language\nconsistent?"]
    LANG --> OUT["_srv_geoArbScore = 0-100\n_srv_geoArbDistance = km"]

    style IN fill:#e67e22,color:#fff
```

## ⑭ DeviceAgeEstimation

```mermaid
flowchart TD
    IN["GPU + Screen\n+ Cores + Memory"] --> GPU["GPU renderer →\nrelease year lookup"]
    IN --> API["WebGL/WebGPU\nAPI version"]
    IN --> HW["Hardware caps\nvs era"]
    GPU --> OLDEST["Oldest component\n= device floor"]
    API --> NEWEST["Newest API\n= device ceiling"]
    HW --> MID["Hardware era\nestimate"]
    OLDEST --> BLEND["Weighted blend"]
    NEWEST --> BLEND
    MID --> BLEND
    BLEND --> OUT["_srv_deviceAge = months\n_srv_deviceEra = label"]

    style IN fill:#e67e22,color:#fff
```

## ⑮ BehavioralReplay

```mermaid
flowchart TD
    IN["TrackingData\nwith timing events"] --> MOUSE["Mouse movement\npattern analysis"]
    IN --> SCROLL["Scroll behavior\nvelocity + jitter"]
    IN --> KEY["Keystroke timing\ncadence analysis"]
    MOUSE --> ENTROPY["Calculate entropy\nof human-like noise"]
    SCROLL --> ENTROPY
    KEY --> ENTROPY
    ENTROPY --> CHECK{"Below bot\nthreshold?"}
    CHECK -->|"Yes"| BOT["_srv_replayScore = low\nSuspected automation"]
    CHECK -->|"No"| HUMAN["_srv_replayScore = high\nLikely human"]

    style IN fill:#e67e22,color:#fff
```

## ⑯ DeadInternet

```mermaid
flowchart TD
    IN["All signals"] --> SYNTH["Synthetic cluster\ndetection"]
    SYNTH --> SAME{"Many identical\nfingerprints?"}
    SAME -->|"Yes"| CLUSTER["Flag fingerprint\ncluster"]
    SAME -->|"No"| SOLO["Individual assessment"]
    CLUSTER --> TIMING["Arrival timing\nanalysis"]
    SOLO --> TIMING
    TIMING --> PATTERN{"Uniform\nintervals?"}
    PATTERN -->|"Yes"| SUS["_srv_deadInternetScore = high\nCoordinated traffic"]
    PATTERN -->|"No"| OK["_srv_deadInternetScore = low"]

    style IN fill:#e67e22,color:#fff
```

## ⑰ LeadQualityScoring (Final)

```mermaid
flowchart TD
    IN["All 16 prior\nenrichment outputs"] --> W1["Bot score\nweight: 25%"]
    IN --> W2["Contradiction score\nweight: 15%"]
    IN --> W3["Stability score\nweight: 15%"]
    IN --> W4["Affluence score\nweight: 10%"]
    IN --> W5["Behavioral replay\nweight: 10%"]
    IN --> W6["Session depth\nweight: 10%"]
    IN --> W7["Cross-customer\nweight: 10%"]
    IN --> W8["Dead internet\nweight: 5%"]
    W1 --> COMPOSITE["Weighted composite\ncalculation"]
    W2 --> COMPOSITE
    W3 --> COMPOSITE
    W4 --> COMPOSITE
    W5 --> COMPOSITE
    W6 --> COMPOSITE
    W7 --> COMPOSITE
    W8 --> COMPOSITE
    COMPOSITE --> GRADE{"Score\nrange?"}
    GRADE -->|"80-100"| A["Grade A\nHigh quality"]
    GRADE -->|"60-79"| B["Grade B\nMedium quality"]
    GRADE -->|"40-59"| C["Grade C\nLow quality"]
    GRADE -->|"0-39"| F["Grade F\nBot / fraud"]
    A --> OUT["_srv_leadScore = N\n_srv_leadGrade = A-F"]
    B --> OUT
    C --> OUT
    F --> OUT

    style IN fill:#e67e22,color:#fff
    style A fill:#2ecc71,color:#fff
    style F fill:#e74c3c,color:#fff
```
