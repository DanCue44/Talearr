import PropTypes from 'prop-types';
import React, { Component } from 'react';
import fetchBookExplanation from './fetchBookExplanation';
import TalearrDecisionStep from './TalearrDecisionStep';
import styles from './TalearrHistoryWhy.css';

const stageByEvent = {
  grabbed: 'release',
  bookFileImported: 'import',
  downloadImported: 'import',
  bookImportIncomplete: 'import',
  downloadFailed: 'import'
};

export function findMatchingSteps(steps, eventType, sourceTitle, downloadId) {
  const stage = stageByEvent[eventType];

  if (!stage || !steps) {
    return [];
  }

  const sameStage = steps.filter((step) => step.stage === stage);
  const byDownload = downloadId ? sameStage.filter((step) => step.downloadId === downloadId) : [];

  if (byDownload.length) {
    return byDownload.slice(0, 2);
  }

  return sourceTitle ? sameStage.filter((step) => step.releaseTitle === sourceTitle).slice(0, 2) : [];
}

// Talearr: the "Why" part of a history entry's details, when one was recorded for it.
class TalearrHistoryWhy extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = { steps: [] };
  }

  componentDidMount() {
    this._mounted = true;

    const { bookId } = this.props;

    if (!bookId || !stageByEvent[this.props.eventType]) {
      return;
    }

    fetchBookExplanation(bookId).then(
      (explanation) => {
        if (this._mounted) {
          const { eventType, sourceTitle, downloadId } = this.props;
          this.setState({ steps: findMatchingSteps(explanation.steps, eventType, sourceTitle, downloadId) });
        }
      },
      () => {}
    );
  }

  componentWillUnmount() {
    this._mounted = false;
  }

  //
  // Render

  render() {
    const { steps } = this.state;

    if (!steps.length) {
      return null;
    }

    return (
      <div className={styles.why}>
        <div className={styles.title}>{'Why'}</div>
        {
          steps.map((step, index) => (
            <TalearrDecisionStep
              key={index}
              step={step}
              showTime={false}
            />
          ))
        }
      </div>
    );
  }
}

TalearrHistoryWhy.propTypes = {
  bookId: PropTypes.number,
  eventType: PropTypes.string.isRequired,
  sourceTitle: PropTypes.string,
  downloadId: PropTypes.string
};

export default TalearrHistoryWhy;
