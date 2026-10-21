import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import formatDateTime from 'Utilities/Date/formatDateTime';
import styles from './TalearrDecisionStep.css';

const stageLabels = {
  metadata: 'Metadata',
  release: 'Release',
  import: 'Import'
};

function getTone(outcome) {
  switch (outcome) {
    case 'matched':
    case 'grabbed':
    case 'imported':
      return { className: styles.good, name: icons.CHECK };
    case 'failed':
      return { className: styles.bad, name: icons.DANGER };
    default:
      return { className: styles.notice, name: icons.WARNING };
  }
}

// Talearr: one step of a book's "Why" story.
function TalearrDecisionStep(props) {
  const {
    step,
    showTime,
    shortDateFormat,
    timeFormat
  } = props;

  const tone = getTone(step.outcome);
  const meta = [stageLabels[step.stage] || step.stage];

  if (showTime && step.createdAt) {
    meta.push(formatDateTime(step.createdAt, shortDateFormat, timeFormat));
  }

  return (
    <div className={styles.step}>
      <div className={styles.icon}>
        <Icon
          className={tone.className}
          name={tone.name}
        />
      </div>

      <div className={styles.content}>
        <div className={styles.summary}>{step.summary}</div>
        <div
          className={styles.meta}
          title={`Reason code: ${step.code}`}
        >
          {meta.join(' • ')}
        </div>

        {
          step.details && step.details.length ?
            <ul className={styles.details}>
              {
                step.details.map((detail, index) => (
                  <li key={index}>{detail}</li>
                ))
              }
            </ul> :
            null
        }
      </div>
    </div>
  );
}

TalearrDecisionStep.propTypes = {
  step: PropTypes.object.isRequired,
  showTime: PropTypes.bool,
  shortDateFormat: PropTypes.string,
  timeFormat: PropTypes.string
};

TalearrDecisionStep.defaultProps = {
  showTime: true
};

export default TalearrDecisionStep;
