for i in 1 2 3 4 5; do
  timeout 420 docker compose -f docker-compose.e2e.yml up --build --abort-on-container-exit --exit-code-from e2e > exp$i.log 2>&1
  echo "run $i exit=$? $(grep -h 'Total tests\|Passed:\|Unable to communicate\|Failed:' exp$i.log | tr '\n' ' ')" >> exp.done
done
echo finished >> exp.done
