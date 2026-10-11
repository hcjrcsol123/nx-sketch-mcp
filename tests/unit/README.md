# 几何算术检查

test_case_expected_values.py、test_local_frame.py、test_unit_conversion.py检查解析体积、局部坐标框架、退化输入和单位转换。运行python -B -m unittest discover -s tests/unit -p 'test_*.py'。这些检查不启动NX，期望值是算术输入，不是实测数据。
