import { Flex } from 'antd';
import styles from './CuttingDownloadPanel.module.css';

export const CuttingDownloadPanel = ({ children }: { children: React.ReactNode }) => (
    <Flex vertical className={styles['cutting-download-panel']}>
        {children}
    </Flex>
);
