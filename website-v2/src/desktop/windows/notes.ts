// The little Markdown the changelog uses, as Notepad shows it with formatting on: headings, paragraphs, list items,
// bold and code. Plain data, so Notepad renders it without HTML strings.

export interface Span {
  text: string;
  bold?: boolean;
  code?: boolean;
}

export interface Block {
  kind: 'h2' | 'h3' | 'p' | 'li';
  spans: Span[];
}

function spans(line: string): Span[] {
  return line
    .split(/(\*\*[^*]+\*\*|`[^`]+`)/)
    .filter(Boolean)
    .map((part) => {
      if (part.startsWith('**') && part.endsWith('**')) return { text: part.slice(2, -2), bold: true };
      if (part.startsWith('`') && part.endsWith('`')) return { text: part.slice(1, -1), code: true };
      return { text: part };
    });
}

export function blocks(markdown: string): Block[] {
  return markdown
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line): Block => {
      if (line.startsWith('### ')) return { kind: 'h3', spans: spans(line.slice(4)) };
      if (line.startsWith('## ')) return { kind: 'h2', spans: spans(line.slice(3)) };
      if (line.startsWith('- ')) return { kind: 'li', spans: spans(line.slice(2)) };
      return { kind: 'p', spans: spans(line) };
    });
}
