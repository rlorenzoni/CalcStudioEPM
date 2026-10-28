"""Writes the manifests and dimensions of Fixtures\\Ipm, a made-up IPM package (application
Vision, cube Plan1) in the format of EPM's own export. The Auto Predict jobs, Calendar.xml and
Events.xml next to them are written by hand; this adds what goes around them:

    Ipm\\IPM\\Export.xml, Import.xml, HP-Vision\\Import.xml, info\\listing.xml, info\\sourceInfo.xml
    Ipm\\.epm\\dimensions.json   members with the IDs the jobs use

    python make_ipm_fixtures.py
"""
import json, os

here = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Ipm')
pkg = os.path.join(here, 'IPM')
hp = os.path.join(pkg, 'HP-Vision')
ARTIFACTS = ('<Artifact recursive="true" parentPath="/Global Artifacts/Auto Predict" pattern="*"/>\n'
             '      <Artifact recursive="false" parentPath="/Global Artifacts" pattern="Calendar"/>\n'
             '      <Artifact recursive="false" parentPath="/Global Artifacts" pattern="Events"/>')
STAMP = ('   <ExportedVersion>26.10.117</ExportedVersion>\n   <ExportedDateUTC>20261027</ExportedDateUTC>\n'
         '   <ExportedTimeUTC>12:00</ExportedTimeUTC>\n   <ExportedBy>demo.user</ExportedBy>\n'
         '   <IDMDomain>demo</IDMDomain>\n   <ServiceInstance>epm</ServiceInstance>\n')


def package(stamp, source, target):
    return ('<?xml version="1.0" encoding="UTF-8"?>\n<Package>\n   <LOCALE>en</LOCALE>\n   <User name="" password=""/>\n'
            + stamp + '   <Task>\n      ' + source + '\n      ' + target + '\n      ' + ARTIFACTS + '\n   </Task>\n</Package>\n')


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)


app = '<Target type="Application" product="HP" project="Default Application Group" application="Vision"/>'
write(os.path.join(pkg, 'Export.xml'), package(
    '', '<Source type="Application" product="HP" project="Default Application Group" application="Vision"/>',
    '<Target type="FileSystem" filePath="/HP-Vision"/>'))
write(os.path.join(pkg, 'Import.xml'), package(STAMP, '<Source type="FileSystem" filePath="/HP-Vision"/>', app))
write(os.path.join(hp, 'Import.xml'), package(STAMP, '<Source type="FileSystem" filePath="/"/>', app))
write(os.path.join(hp, 'info', 'sourceInfo.xml'),
      '<?xml version="1.0" encoding="UTF-8"?>\n<sourceInfo>\n   <Application>Vision</Application>\n   <Product>HP</Product>\n'
      '   <ProductVersion>11.1.2.3</ProductVersion>\n   <Project>Default Application Group</Project>\n'
      '   <usesFriendlyNames>false</usesFriendlyNames>\n   <metadataFileSupported>false</metadataFileSupported>\n'
      '   <groupingSupported>true</groupingSupported>\n   <LCMVersion>11.1.2</LCMVersion>\n</sourceInfo>\n')


def resource(name, kind, path, desc=''):
    return (f'<resource name="{name}" id="{name}" type="{kind}" cloneOnly="false" size="" path="{path}" pathAlias="{path}" '
            f'modifiedBy="demo.user" lastUpdated="1793000000000" description="{desc}" />')


def folder(name, path):
    return (f'<folder name="{name}" type="Folder" size="" id="" path="{path}" pathAlias="{path}" modifiedBy="" '
            'lastUpdated="" description="" />')


write(os.path.join(hp, 'info', 'listing.xml'),
      '<?xml version="1.0" encoding="utf-8"?><artifactListing>'
      + resource('Calendar', 'IPM Calendar', '/Global Artifacts')
      + resource('Events', 'IPM Event', '/Global Artifacts')
      + resource('Revenue - Advanced', 'APJob', '/Global Artifacts/Auto Predict', 'Revenue predicted from units and price')
      + resource('Revenue - Forecast', 'APJob', '/Global Artifacts/Auto Predict', 'Univariate forecast with insights')
      + folder('Global Artifacts', '/Global Artifacts') + folder('Auto Predict', '/Global Artifacts/Auto Predict')
      + '</artifactListing>')

# Members with IDs (the jobs' outputs and drivers), and members without (the Model Scope's)
members = {
    'Account':  [('Revenue', 'aaaaaaaa-0000-4000-8000-000000000001', 'Total Revenue'),
                 ('Units', 'aaaaaaaa-0000-4000-8000-000000000002', 'Total Revenue'),
                 ('Price', 'aaaaaaaa-0000-4000-8000-000000000003', 'Total Revenue'),
                 ('Cost, Freight', 'aaaaaaaa-0000-4000-8000-000000000004', 'Total Revenue')],
    'Entity':   [('North', 'eeeeeeee-0000-4000-8000-000000000001', 'Total Entity'),
                 ('South', 'eeeeeeee-0000-4000-8000-000000000002', 'Total Entity'),
                 ('No Entity', 'eeeeeeee-0000-4000-8000-000000000003', 'Entity')],
    'Product':  [('Product A', 'bbbbbbbb-0000-4000-8000-000000000001', 'Total Product'),
                 ('Product B', 'bbbbbbbb-0000-4000-8000-000000000002', 'Total Product')],
    'Currency': [('USD', 'cccccccc-0000-4000-8000-000000000001', 'Currency'),
                 ('No Currency', 'cccccccc-0000-4000-8000-000000000002', 'Currency')],
    'Version':  [('Final', None, 'Version'), ('Working', None, 'Version'), ('Predicted', None, 'Version')],
    'Scenario': [('Actual', None, 'Scenario'), ('Plan', None, 'Scenario'), ('Forecast', None, 'Scenario')],
    'View':     [('Periodic', None, 'View')],
    'Years':    [('FY26', None, 'Years')],
    'Period':   [('Jan', None, 'YearTotal')],
}
cube = {}
for dim, ms in members.items():
    cube[dim] = {
        'Name': dim, 'DimType': 'Generic', 'Members': [m for m, _, _ in ms], 'Aliases': [],
        'Nodes': [dict({'n': m, 'p': p}, **({'f': {'id': i}} if i else {})) for m, i, p in ms],
    }
write(os.path.join(here, '.epm', 'dimensions.json'), json.dumps(
    {'PulledAt': '2026-10-27T00:00:00', 'Cubes': {'Plan1': list(members)}, 'ByCube': {'Plan1': cube}, 'ByName': {}}, indent=1))
print('Fixtures\\Ipm written')
