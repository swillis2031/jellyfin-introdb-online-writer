"""Offline smoke test for the Python upload client's parser and request shape."""
import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
from unittest.mock import patch

ROOT=pathlib.Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('uploader',ROOT/'tools/upload_segments_online.py')
uploader=importlib.util.module_from_spec(spec)
spec.loader.exec_module(uploader)

class UploaderTest(unittest.TestCase):
 def test_dry_run_only(self):
  with tempfile.TemporaryDirectory() as d:
   file=pathlib.Path(d)/'pilot_checked.jsonl'
   file.write_text(json.dumps({'item_id':'1cb44cda-d143-0b93-28c1-de768a2562c0','segments':[{'type':'Intro','start_ticks':60000000,'end_ticks':100000000}]})+'\n')
   with patch.dict(os.environ,{'JELLYFIN_API_KEY':'abc','JELLYFIN_URL':'http://localhost:8096'}):
    with patch('sys.argv',['x','--input',str(file),'--max-items','5']):
     with patch.object(uploader,'request',side_effect=[{'Plugin':'IntroDB Online Writer'},{'DryRun':True,'EpisodesToInsert':1,'SegmentsToInsert':1}]) as req:
      uploader.main()
      self.assertEqual(req.call_count,2)
      self.assertIn('dryRun=true',req.call_args.args[2])

if __name__=='__main__': unittest.main()
