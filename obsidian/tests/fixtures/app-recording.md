---
docuclick: diagramm
---

## Schritte

%% DocuClick: Schritte werden aus dem Diagramm erzeugt, Änderungen hier gehen verloren. %%
1. Linksklick auf „Anmelden“
2. Abzweigung:
	- **Pfad: Erfolg**
		1. Linksklick auf „Weiter“
%% DocuClick: Ende der Schritte %%

%% DocuClick-Diagrammdaten (nicht von Hand ändern)
{
  "format": "docuclick-diagram",
  "version": 1,
  "canvas": {
    "nodes": [
      {
        "id": "e775fb09546a49fbbe2edb0420f72802",
        "type": "group",
        "x": -8,
        "y": -8,
        "width": 396,
        "height": 356
      },
      {
        "id": "05a01e9d0bf6469ca2cf5e9c270126e3",
        "type": "text",
        "text": "Linksklick auf „Anmelden“",
        "x": 0,
        "y": 0,
        "width": 380,
        "height": 60
      },
      {
        "id": "b72096e4948c415db73f05d92842cf45",
        "type": "file",
        "x": 0,
        "y": 70,
        "width": 380,
        "height": 270
      },
      {
        "id": "a75c4a7bc5234575bd2037ef35b70b1a",
        "type": "text",
        "text": "◆ Abzweigung",
        "x": 0,
        "y": 400,
        "width": 380,
        "height": 60,
        "color": "6"
      },
      {
        "id": "acf50c3eede9448f9d36e5608c908ffb",
        "type": "text",
        "text": "↳ Pfad: Erfolg",
        "x": 460,
        "y": 520,
        "width": 380,
        "height": 60,
        "color": "4"
      },
      {
        "id": "ac9858628b4a4032b4e41fb2ca16a7d6",
        "type": "group",
        "x": 452,
        "y": 632,
        "width": 396,
        "height": 356
      },
      {
        "id": "f88fa8645f5c4bc0b898ca255cf4537b",
        "type": "text",
        "text": "Linksklick auf „Weiter“",
        "x": 460,
        "y": 640,
        "width": 380,
        "height": 60
      },
      {
        "id": "463d213c2a72405e8019223e57068f7a",
        "type": "file",
        "x": 460,
        "y": 710,
        "width": 380,
        "height": 270
      }
    ],
    "edges": [
      {
        "id": "fd2d45985a5440bcba833afbaeb82f9e",
        "fromNode": "05a01e9d0bf6469ca2cf5e9c270126e3",
        "toNode": "a75c4a7bc5234575bd2037ef35b70b1a",
        "fromSide": "bottom",
        "toSide": "top"
      },
      {
        "id": "75773e7a28fa4e30b095dbbc605b8caa",
        "fromNode": "a75c4a7bc5234575bd2037ef35b70b1a",
        "toNode": "acf50c3eede9448f9d36e5608c908ffb",
        "fromSide": "bottom",
        "toSide": "top"
      },
      {
        "id": "02b7166be030439cbefa011710b272aa",
        "fromNode": "acf50c3eede9448f9d36e5608c908ffb",
        "toNode": "f88fa8645f5c4bc0b898ca255cf4537b",
        "fromSide": "bottom",
        "toSide": "top"
      },
      {
        "id": "e50f106d1dc7432d87603a9faa71a866",
        "fromNode": "05a01e9d0bf6469ca2cf5e9c270126e3",
        "toNode": "f88fa8645f5c4bc0b898ca255cf4537b",
        "fromSide": "bottom",
        "toSide": "top",
        "docuClickManual": true,
        "lineStyle": "solid"
      }
    ]
  },
  "flow": {
    "nodes": [
      {
        "data": {
          "id": "05a01e9d0bf6469ca2cf5e9c270126e3",
          "label": "Linksklick auf „Anmelden“",
          "color": "#2563EB",
          "shape": "round-rectangle",
          "stepIndex": 1
        },
        "position": {
          "x": 0,
          "y": 0
        }
      },
      {
        "data": {
          "id": "a75c4a7bc5234575bd2037ef35b70b1a",
          "label": "◆ Abzweigung",
          "color": "#6B7280",
          "shape": "diamond",
          "stepIndex": 2
        },
        "position": {
          "x": 0,
          "y": 400
        }
      },
      {
        "data": {
          "id": "acf50c3eede9448f9d36e5608c908ffb",
          "label": "↳ Pfad: Erfolg",
          "color": "#0891B2",
          "shape": "round-rectangle",
          "stepIndex": 3
        },
        "position": {
          "x": 460,
          "y": 520
        }
      },
      {
        "data": {
          "id": "f88fa8645f5c4bc0b898ca255cf4537b",
          "label": "Linksklick auf „Weiter“",
          "color": "#0891B2",
          "shape": "round-rectangle",
          "stepIndex": 4
        },
        "position": {
          "x": 460,
          "y": 640
        }
      }
    ],
    "edges": [
      {
        "data": {
          "id": "05a01e9d0bf6469ca2cf5e9c270126e3->a75c4a7bc5234575bd2037ef35b70b1a",
          "source": "05a01e9d0bf6469ca2cf5e9c270126e3",
          "target": "a75c4a7bc5234575bd2037ef35b70b1a",
          "color": "#6B7280",
          "manual": false,
          "lineStyle": "solid"
        }
      },
      {
        "data": {
          "id": "a75c4a7bc5234575bd2037ef35b70b1a->acf50c3eede9448f9d36e5608c908ffb",
          "source": "a75c4a7bc5234575bd2037ef35b70b1a",
          "target": "acf50c3eede9448f9d36e5608c908ffb",
          "color": "#0891B2",
          "manual": false,
          "lineStyle": "solid"
        }
      },
      {
        "data": {
          "id": "acf50c3eede9448f9d36e5608c908ffb->f88fa8645f5c4bc0b898ca255cf4537b",
          "source": "acf50c3eede9448f9d36e5608c908ffb",
          "target": "f88fa8645f5c4bc0b898ca255cf4537b",
          "color": "#0891B2",
          "manual": false,
          "lineStyle": "solid"
        }
      },
      {
        "data": {
          "id": "manual-05a01e9d0bf6469ca2cf5e9c270126e3->f88fa8645f5c4bc0b898ca255cf4537b",
          "source": "05a01e9d0bf6469ca2cf5e9c270126e3",
          "target": "f88fa8645f5c4bc0b898ca255cf4537b",
          "color": "#2563EB",
          "manual": true,
          "lineStyle": "solid"
        }
      }
    ]
  },
  "images": {
    "05a01e9d0bf6469ca2cf5e9c270126e3": "Prozesse/Attachments/Ablauf/182142_895.png",
    "f88fa8645f5c4bc0b898ca255cf4537b": "Prozesse/Attachments/Ablauf/182142_914.png"
  }
}
%%
