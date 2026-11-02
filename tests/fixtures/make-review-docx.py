#!/usr/bin/env python3
"""Generate a small Word review document for tests/docx-smoke.sh.

Hand-built OOXML, no dependencies. It exercises every review item --from-docx knows: comments
(a reply thread, a resolved comment, a comment with no range, one in a table cell, the second of
two identical phrases, one running over a page break), and tracked changes (a replacement, plain
insertions and deletions, a deletion over the page break, an inserted paragraph, a run-format
change, a move). A running header and footer test that a range continued over a page break
does not pick them up. At the end: anchors ending the last paragraph of a footnote and of a text
box (which LibreOffice drops unless something follows them), table rows inserted and deleted, a
whole inserted table, and paragraph-, table- and section-property changes.

Layout must not depend on the machine's fonts: the text is set in Liberation Serif (CI installs
fonts-liberation), and the two ranges that must cross a page break each contain a hard page
break, so they cross one whatever the metrics. Left to the default font, LibreOffice substitutes
whatever the machine has for Calibri -- on a bare runner a maths font with huge line boxes.

Usage: make-review-docx.py <output.docx>
"""
import zipfile, sys
W='xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" xmlns:w15="http://schemas.microsoft.com/office/word/2012/wordml" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:v="urn:schemas-microsoft-com:vml"'
D='w:date="2026-10-30T10:00:00Z"'
def r(t, rpr=''): return f'<w:r>{rpr}<w:t xml:space="preserve">{t}</w:t></w:r>'
def p(*xs, ppr=''): return f'<w:p>{ppr}{"".join(xs)}</w:p>'
def cs(i): return f'<w:commentRangeStart w:id="{i}"/>'
def ce(i): return f'<w:commentRangeEnd w:id="{i}"/><w:r><w:commentReference w:id="{i}"/></w:r>'
def ins(i,a,t): return f'<w:ins w:id="{i}" w:author="{a}" {D}>{r(t)}</w:ins>'
def dele(i,a,t): return f'<w:del w:id="{i}" w:author="{a}" {D}><w:r><w:delText xml:space="preserve">{t}</w:delText></w:r></w:del>'
filler="Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. "
body=[]
body.append(p(r("The quick "),cs(0),r("brown fox"),ce(0),r(" jumps over the lazy dog.")))
body.append(p(r("We measured "),dele(10,"Carol","three"),ins(11,"Carol","four"),r(" samples in total."),ins(12,"Dave"," This sentence was inserted.")))
body.append(p(r("Keep this, "),dele(13,"Dave","remove this phrase, "),r("and keep the rest.")))
body.append(p(r("The quick brown fox appears again; this time "),cs(3),r("brown fox"),ce(3),r(" is commented on the second occurrence.")))
body.append(p(r("This word is "),f'<w:r><w:rPr><w:b/><w:rPrChange w:id="20" w:author="Erin" {D}><w:rPr/></w:rPrChange></w:rPr><w:t>important</w:t></w:r>',r(" now.")))
body.append(p(f'<w:moveFrom w:id="21" w:author="Frank" {D}>{r("Moved sentence. ")}</w:moveFrom>',r("Stays put.")))
body.append(p(r("A point comment sits here"),f'<w:r><w:commentReference w:id="4"/></w:r>',r(" without a range.")))
body.append(p(r("Resolved: "),cs(5),r("this was fixed"),ce(5),r(".")))
body.append(p(ins(30,"Gina","A whole inserted paragraph."),ppr=f'<w:pPr><w:rPr><w:ins w:id="31" w:author="Gina" {D}/></w:rPr></w:pPr>'))
body.append(f'<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders><w:top w:val="single" w:sz="4"/><w:bottom w:val="single" w:sz="4"/><w:insideV w:val="single" w:sz="4"/></w:tblBorders></w:tblPr><w:tblGrid><w:gridCol w:w="3000"/><w:gridCol w:w="3000"/></w:tblGrid><w:tr><w:tc>{p(r("Cell A1"))}</w:tc><w:tc>{p(r("Cell "),cs(6),r("B1 value"),ce(6))}</w:tc></w:tr></w:tbl>')
PAGE='<w:r><w:br w:type="page"/></w:r>'
for k in range(3): body.append(p(r(filler*3)))
body.append(p(r(filler+"Here begins "),cs(7),r("a comment that runs "+filler),PAGE,r(filler+"until here"),ce(7),r(". And "),f'<w:del w:id="40" w:author="Hank" {D}><w:r><w:delText xml:space="preserve">a deletion that also runs {filler}</w:delText></w:r><w:r><w:br w:type="page"/></w:r><w:r><w:delText xml:space="preserve">{filler}over the break </w:delText></w:r></w:del>',r("ends.")))
body.append(p(r("Final paragraph: the moved text lands here: "),f'<w:moveTo w:id="22" w:author="Frank" {D}>{r("Moved sentence. ")}</w:moveTo>'))
body.append(p(r("This sentence has a footnote"),'<w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:footnoteReference w:id="1"/></w:r>',r(".")))
body.append(p(r("A text box: "),f'<w:r><w:pict><v:shape id="tb1" type="#_x0000_t202" style="width:220pt;height:30pt"><v:textbox><w:txbxContent>{p(r("Inside the "),cs(8),r("text box words"),ce(8))}</w:txbxContent></v:textbox></v:shape></w:pict></w:r>'))
TB=lambda change='': f'<w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders><w:top w:val="single" w:sz="4"/><w:bottom w:val="single" w:sz="4"/><w:insideH w:val="single" w:sz="4"/><w:insideV w:val="single" w:sz="4"/></w:tblBorders>{change}</w:tblPr><w:tblGrid><w:gridCol w:w="3000"/><w:gridCol w:w="3000"/></w:tblGrid>'
row=lambda a,b,trpr='': f'<w:tr>{trpr}<w:tc>{p(a)}</w:tc><w:tc>{p(b)}</w:tc></w:tr>'
body.append(f'<w:tbl>{TB(f"""<w:tblPrChange w:id="90" w:author="Tom" {D}><w:tblPr><w:tblW w:w="0" w:type="auto"/></w:tblPr></w:tblPrChange>""")}'
    + row(r("Keep A1"),r("Keep B1"))
    + row(ins(91,"Rob","New A"),ins(92,"Rob","New B"),f'<w:trPr><w:ins w:id="93" w:author="Rob" {D}/></w:trPr>')
    + row(dele(94,"Rob","Old A"),dele(95,"Rob","Old B"),f'<w:trPr><w:del w:id="96" w:author="Rob" {D}/></w:trPr>')
    + row(r("Keep A4"),r("Keep B4")) + '</w:tbl>')
body.append(p(r("A centred paragraph that used to be left aligned."),ppr=f'<w:pPr><w:jc w:val="center"/><w:pPrChange w:id="97" w:author="Pat" {D}><w:pPr/></w:pPrChange></w:pPr>'))
body.append(p(r("Before an inserted table.")))
TI=lambda i: f'<w:trPr><w:ins w:id="{i}" w:author="Tia" {D}/></w:trPr>'
body.append(f'<w:tbl>{TB()}' + row(ins(101,"Tia","T1"),ins(102,"Tia","T2"),TI(100)) + row(ins(104,"Tia","T3"),ins(105,"Tia","T4"),TI(103)) + '</w:tbl>')
body.append(p(r("After the inserted table.")))
sect=f'<w:sectPr><w:headerReference w:type="default" r:id="rIdH"/><w:footerReference w:type="default" r:id="rIdF"/><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720"/><w:pgNumType w:start="1"/><w:sectPrChange w:id="110" w:author="Sam" {D}><w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720"/></w:sectPr></w:sectPrChange></w:sectPr>'
doc=f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document {W}><w:body>{"".join(body)}{sect}</w:body></w:document>'
def cm(i,a,t,pid,date=D): return f'<w:comment w:id="{i}" w:author="{a}" {date}><w:p w14:paraId="{pid}"><w:r><w:t xml:space="preserve">{t}</w:t></w:r></w:p></w:comment>'
comments=f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:comments {W}>'+cm(0,"Alice",'Is "brown" the right colour?',"00000001")+cm(1,"Bob","Yes, keep it.","00000002",'w:date="2026-10-31T09:00:00Z"')+cm(3,"Alice","Second occurrence.","00000003")+cm(4,"Ivy","A point comment.","00000004")+cm(5,"Jack","Done now.","00000005")+cm(6,"Kim","Check this cell.","00000006")+cm(7,"Leo","This spans the page break.","00000007")+cm(8,"Tex","In the text box.","00000008")+cm(9,"Fay","In the footnote.","00000009")+'</w:comments>'
fn=f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:footnotes {W}><w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote><w:footnote w:id="1">{p(r("Footnote text with "),cs(9),r("noted words"),ce(9),r(" inside."),dele(98,"Fay"," deleted in note"))}</w:footnote></w:footnotes>'
cx=f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w15:commentsEx {W}><w15:commentEx w15:paraId="00000001" w15:done="0"/><w15:commentEx w15:paraId="00000002" w15:paraIdParent="00000001" w15:done="0"/><w15:commentEx w15:paraId="00000005" w15:done="1"/></w15:commentsEx>'
hdr=f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:hdr {W}>{p(r("RUNNING HEADER TEXT"))}</w:hdr>'
ftr=f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:ftr {W}>{p(r("Running footer text"))}</w:ftr>'
styles=f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles {W}><w:docDefaults><w:rPrDefault><w:rPr>'\
    '<w:rFonts w:ascii="Liberation Serif" w:hAnsi="Liberation Serif" w:cs="Liberation Serif" w:eastAsia="Liberation Serif"/>'\
    '<w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults></w:styles>'
ct='''<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/comments.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml"/><Override PartName="/word/commentsExtended.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.commentsExtended+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/><Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/><Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"/></Types>'''
rels='''<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'''
drels='''<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments" Target="comments.xml"/><Relationship Id="rId2" Type="http://schemas.microsoft.com/office/2011/relationships/commentsExtended" Target="commentsExtended.xml"/><Relationship Id="rIdS" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rIdH" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/><Relationship Id="rIdF" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/><Relationship Id="rIdN" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes" Target="footnotes.xml"/></Relationships>'''
with zipfile.ZipFile(sys.argv[1],'w') as z:
    for n,c in [('[Content_Types].xml',ct),('_rels/.rels',rels),('word/document.xml',doc),('word/_rels/document.xml.rels',drels),('word/styles.xml',styles),('word/comments.xml',comments),('word/commentsExtended.xml',cx),('word/header1.xml',hdr),('word/footer1.xml',ftr),('word/footnotes.xml',fn)]:
        info = zipfile.ZipInfo(n, date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, c)
