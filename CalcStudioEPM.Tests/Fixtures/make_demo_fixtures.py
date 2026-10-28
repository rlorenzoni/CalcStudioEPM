"""Builds Fixtures\\Demo from a pulled connection of Oracle's EPBCS demo application.

    python make_demo_fixtures.py <pulled connection folder>     e.g. C:\\TEMP_NPP_EPM\\DEMO

Run once, on a package already downloaded: the tests never need the connection or EPM.
Keeps a few artifacts of each kind the pull handles (Calc Script, Groovy and graphical rules,
graphical and script templates, a Script, a RuleSet), with listing.xml narrowed to them, adds two
made-up rules — Nested Branches (a step 12 IFs deep) and Empty Rule (an empty script) — and
replaces the instance names in the manifests. The tests extract them with the current code and check that nothing reads back
as changed.
"""
import os, re, shutil, sys
import xml.etree.ElementTree as ET

KEEP = {
    ('/Planning/EPBCS/Plan1/Rules', 'Clear Actuals'),                    # graphical rule
    ('/Planning/EPBCS/Plan1/Rules', 'Generate Plan'),                    # graphical rule
    ('/Planning/EPBCS/Plan1/Rules', 'DataExport_Test'),                  # Calc Script
    ('/Planning/EPBCS/OEP_FS/Rules', 'I/C Eliminations'),                # "/" in the name
    ('/Planning/EPBCS/OEP_FS/Rules', 'Alllocate New Mix'),               # Calc Script
    ('/Planning/EPBCS/OEP_FS/Rules', 'Planning to SM'),                  # Groovy
    ('/Planning/EPBCS/OEP_CPX/Templates', 'OCX_AddAsset_Temp'),          # graphical template
    ('/Planning/EPBCS/OEP_FS/Templates', 'OFS_GridMemberSelection_GT'),  # Groovy template
    ('/Planning/EPBCS/OEP_PFP/Scripts', 'OPF_Aggregation - Calculate Revenue'),
    ('/Planning/EPBCS/RuleSets', 'Update Rates'),                        # RuleSet
}
CALC = 'CALC-Calculation Manager'


def file_name(name):
    return ''.join('%%%02X' % ord(c) if c in '/\\:*?"<>|' else c for c in name)


NESTED = 'Nested Branches'   # made up: IFs nested 12 deep in ELSE branches, a script step in each THEN
EMPTY = 'Empty Rule'          # made up: a script rule whose script was never written (like the demo's z_temp_pfp)
PROPS = '<property name="application">EPBCS</property><property name="plantype">Plan1</property>'


def rule_xml(name, body):
    return ("<?xml version = '1.0' encoding = 'UTF-8'?>\n<HBRRepo><variables/><rulesets/><rules>"
            f'<rule id="1" name="{name}" product="Planning">{PROPS}{body}</rule></rules><components/><deployobjects>'
            f'<deployobject product="2" application="epbcs" plantype="plan1" obj_id="1" obj_type="1" name="{name.upper()}"/>'
            '</deployobjects></HBRRepo>')


def nested_rule(levels=12):
    """IF › ELSE › IF › ELSE … 12 deep, each THEN holding a script step: the shallow steps have
    short numbers ("01 1-THEN 01"), the deepest ~120 characters, like the deepest steps of the
    demo's OWP_Change Existing Details_T."""
    def test(n):
        return ('<test><expression><biop type="eq"><expression seq="1"><user_inp>Application</user_inp></expression>'
                f'<expression seq="2"><user_inp>Level {n}</user_inp></expression></biop></expression></test>')

    def step(n):
        caption = 'Deepest step' if n == levels else f'Level {n} step'
        return (f'<statement seq="1" type="component" kind="script" name=""><component type="script" id="-{100 + n}" name="" '
                f'product="Planning">{PROPS}<property name="caption">{caption}</property><script type="calcscript">'
                f'FIX ("OEP_Actual", "OEP_Working")\n   CLEARDATA "OEP_Plan{n}";\nENDFIX\n</script></component></statement>')
    body = ''
    for n in range(levels, 0, -1):
        other = f'<else>{body}</else>' if body else ''
        body = (f'<statement seq="1" type="if"><if product="Planning">{PROPS}<property name="caption">Level {n}</property>'
                f'{test(n)}<then>{step(n)}</then>{other}</if></statement>')
    return rule_xml(NESTED, body)


def sanitize(text):
    text = re.sub(r'<IDMDomain>[^<]*</IDMDomain>', '<IDMDomain>demo</IDMDomain>', text)
    return re.sub(r'<ServiceInstance>[^<]*</ServiceInstance>', '<ServiceInstance>epm</ServiceInstance>', text)


def main(src):
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, 'Demo', 'CalcManager')
    shutil.rmtree(os.path.join(here, 'Demo'), ignore_errors=True)
    os.makedirs(os.path.join(out, CALC, 'info'))
    cm = os.path.join(src, 'CalcManager')

    for f in ('Export.xml', 'Import.xml', os.path.join(CALC, 'Import.xml'), os.path.join(CALC, 'info', 'sourceInfo.xml')):
        p = os.path.join(cm, f)
        if os.path.exists(p):
            with open(p, encoding='utf-8') as r, open(os.path.join(out, f), 'w', encoding='utf-8', newline='') as w:
                w.write(sanitize(r.read()))

    listing = ET.parse(os.path.join(cm, CALC, 'info', 'listing.xml'))
    root = listing.getroot()
    kept = set()
    for r in list(root.iter('resource')):
        key = (r.get('path'), r.get('name'))
        if key not in KEEP:
            root.remove(r)
            continue
        rel = os.path.join(*r.get('path').strip('/').split('/'), file_name(r.get('name')))
        os.makedirs(os.path.dirname(os.path.join(out, CALC, 'resource', rel)), exist_ok=True)
        shutil.copyfile(os.path.join(cm, CALC, 'resource', rel), os.path.join(out, CALC, 'resource', rel))
        kept.add(key)
    missing = KEEP - kept
    if missing:
        sys.exit('Not in the pulled listing: %s' % sorted(missing))

    # The made-up rules, listed like the demo's
    like = next(r for r in root.iter('resource') if r.get('name') == 'Clear Actuals')
    for name, xml in ((NESTED, nested_rule()), (EMPTY, rule_xml(EMPTY, '<script type="calcscript"/>'))):
        ET.SubElement(root, 'resource', dict(like.attrib, name=name, id=name)).tail = like.tail
        with open(os.path.join(out, CALC, 'resource', *like.get('path').strip('/').split('/'), name), 'w',
                  encoding='utf-8', newline='') as w:
            w.write(xml)
        kept.add((like.get('path'), name))
    listing.write(os.path.join(out, CALC, 'info', 'listing.xml'), encoding='utf-8', xml_declaration=True)
    print('Fixtures\\Demo: %d artifacts' % len(kept))


if __name__ == '__main__':
    main(sys.argv[1])
